using ValveResourceFormat.ResourceTypes.SmartProps.Elements;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Creates typed smart prop elements by their Source 2 class name.
    /// </summary>
    static class SmartPropElementFactory
    {
        // These can all be found in the smartprops schema

        private static readonly Dictionary<string, Func<SmartPropDefinitionParser, SmartPropElement>> Elements = new()
        {
            ["CSmartPropElement_Model"] = parse => new ModelElement(parse),
            ["CSmartPropElement_PropDynamic"] = parse => new ModelElement(parse),
            ["CSmartPropElement_PropPhysics"] = parse => new ModelElement(parse),
            ["CSmartPropElement_Group"] = parse => new GroupElement(parse),
            ["CSmartPropElement_Deformer"] = parse => new GroupElement(parse),
            ["CSmartPropElement_ModifyState"] = parse => new ModifyStateElement(parse),
            ["CSmartPropElement_PickOne"] = parse => new PickOneElement(parse),
            ["CSmartPropElement_SmartProp"] = parse => new NestedSmartPropElement(parse),
            ["CSmartPropElement_FitOnLine"] = parse => new FitOnLineElement(parse),
            ["CSmartPropElement_PlaceMultiple"] = parse => new PlaceMultipleElement(parse),
            ["CSmartPropElement_PlaceOnPath"] = parse => new PlaceOnPathElement(parse),
            ["CSmartPropElement_PlaceInSphere"] = parse => new PlaceInSphereElement(parse),
            ["CSmartPropElement_Layout2DGrid"] = parse => new Layout2DGridElement(parse),
            ["CSmartPropElement_BendDeformer"] = parse => new BendDeformerElement(parse),
            ["CSmartPropElement_MidpointDeformer"] = parse => new MidpointDeformerElement(parse),
            ["CSmartPropElement_PlaceOnMesh"] = parse => new PlaceOnMeshElement(parse),
            ["Hammer5Tools_Comment"] = parse => new CommentElement(parse),
        };

        public static SmartPropElement Create(SmartPropDefinitionParser parse)
        {
            var className = parse.RawString("_class", string.Empty);

            return Elements.TryGetValue(className, out var factory)
                ? factory(parse)
                : new UnknownElement(parse, className);
        }
    }
}
