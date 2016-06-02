﻿namespace Persimmon

open System
open System.Reflection
open FSharp.Reflection
open FSharp.Object.Diff

module internal DU =

  [<Literal>]
  let Tag = "Tag"

  [<Literal>]
  let Item = "Item"

[<Sealed>]
type UnionCaseItemElementSelector(propertyName: string) =
  inherit ElementSelector()

  override __.HumanReadableString =
    if propertyName.StartsWith(DU.Item) then propertyName.Replace(DU.Item, "")
    else propertyName
  member __.PropertyName = propertyName
  override this.Equals(other) =
    match other with
    | null -> false
    | :? UnionCaseItemElementSelector as other ->
      if obj.ReferenceEquals(this, other) then true
      else this.PropertyName = other.PropertyName
    | _ -> false
  override __.GetHashCode() = hash propertyName

type UnionCaseFieldAccessor =
  | UnionCaseTagAccessor of PropertyInfo
  | UnionCaseItemAccessor of PropertyAwareAccessor
with
  interface PropertyAwareAccessor with
    member this.Type =
      match this with
      | UnionCaseTagAccessor info -> info.PropertyType
      | UnionCaseItemAccessor accessor -> accessor.Type
    member __.GetPropertyAttribute() = null
    member __.PropertyAttributes = Seq.empty
    member this.PropertyName =
      match this with
      | UnionCaseTagAccessor info -> info.Name
      | UnionCaseItemAccessor accessor -> accessor.PropertyName
    member __.Set(_, _) = ()
    member __.Unset(_) = ()
    member __.CategoriesFromAttribute = Set.empty
    member this.Get(target) =
      if target = null then null
      else
        match this with
        | UnionCaseTagAccessor info ->
          let t = info.DeclaringType
          let tag =
            try
              info.GetValue(target, [||]) |> unbox<int>
            with e ->
              raise <| PropertyReadException(info.Name, target.GetType(), e)
          FSharpType.GetUnionCases(t)
           |> Array.pick (fun x -> if x.Tag = tag then Some(box (t.Name + "." + x.Name)) else None)
        | UnionCaseItemAccessor accessor -> accessor.Get(target)
    member this.ElementSelector =
      match this with
      | UnionCaseTagAccessor info ->
        BeanPropertyElementSelector(info.Name) :> ElementSelector
      | UnionCaseItemAccessor accessor ->
        UnionCaseItemElementSelector(accessor.PropertyName) :> ElementSelector

[<Sealed>]
type DiscriminatedUnionDiffer(
                              differDispatcher: DifferDispatcher,
                              isIntrospectableResolver: IsIntrospectableResolver,
                              isReturnableResolver: IsReturnableResolver,
                              comparisonStrategyResolver: ComparisonStrategyResolver,
                              typeInfoResolver: TypeInfoResolver
  ) =

  let compareUsingIntrospection (node: DiffNode) (instances: Instances) =
    let typeInfo = typeInfoResolver.TypeInfoForNode(node)
    node.TypeInfo <- typeInfo
    for accessor in typeInfo.Accessors do
      let propertyNode = differDispatcher.Dispatch(node, instances, UnionCaseItemAccessor accessor)
      if isReturnableResolver.IsReturnable(propertyNode) then
        node.AddChild(propertyNode)

  let compareDUTagProperty (node: DiffNode) (instances: Instances) =
    let accessor = UnionCaseTagAccessor(instances.Type.GetProperty(DU.Tag))
    let propertyNode = differDispatcher.Dispatch(node, instances, accessor)
    if isReturnableResolver.IsReturnable(propertyNode) then
      node.AddChild(propertyNode)

  let compare (node: DiffNode) (instances: Instances) =
    match instances.Base, instances.Working with
    | null, null -> compareUsingIntrospection node instances
    | _, null | null, _ -> compareDUTagProperty node instances
    | b, w ->
      let baseInfo, _ = FSharpValue.GetUnionFields(b, instances.Type)
      let workingInfo, _ = FSharpValue.GetUnionFields(w, instances.Type)
      if baseInfo <> workingInfo then compareDUTagProperty node instances
      else compareUsingIntrospection node instances

  let compareUsingAppropriateMethod (node: DiffNode) (instances: Instances) =
    let comparisonStrategy = comparisonStrategyResolver.ResolveComparisonStrategy(node)
    if comparisonStrategy <> null then
     comparisonStrategy.Compare(node, instances.Type, instances.Working, instances.Base)
    elif isIntrospectableResolver.IsIntrospectable(node) then
      compare node instances

  member __.Accepts(typ: Type) =
    match Collection.FSharpList.cast typ with
    | None ->
      match Collection.ICollection.cast typ with
      | None when FSharpType.IsUnion(typ) -> true
      | _ -> false
    | _ -> false

  member __.Compare(parentNode, instances: Instances) =
    let node = DiffNode(parentNode, instances.SourceAccessor, instances.Type)
    if instances.AreNull || instances.AreSame then
      node.State <- Untouched
    elif instances.HasBeenAdded then
      compareUsingAppropriateMethod node instances
      node.State <- Added
    elif instances.HasBeenRemoved then
      compareUsingAppropriateMethod node instances
      node.State <- Removed
    else compareUsingAppropriateMethod node instances
    node

  interface Differ with

    member this.Accepts(typ: Type) = this.Accepts(typ)

    member this.Compare(parentNode, instances) = this.Compare(parentNode, instances)
