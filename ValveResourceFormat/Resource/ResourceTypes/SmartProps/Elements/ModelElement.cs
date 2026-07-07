namespace ValveResourceFormat.ResourceTypes.SmartProps.Elements
{
    /// <summary>
    /// Places a model at the current transform.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_Model">CSmartPropElement_Model</seealso>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_PropDynamic">CSmartPropElement_PropDynamic</seealso>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropElement_PropPhysics">CSmartPropElement_PropPhysics</seealso>
    sealed class ModelElement : SmartPropElement
    {
        private readonly StringAttribute modelName;
        private readonly VectorAttribute modelScale;
        private readonly FloatAttribute uniformModelScale;
        private readonly StringAttribute materialGroupName;
        private readonly long lodLevel;

        public ModelElement(SmartPropDefinitionParser parse) : base(parse)
        {
            modelName = parse.String("m_sModelName");
            modelScale = parse.Vector("m_vModelScale", Vector3.One);
            uniformModelScale = parse.Float("m_flUniformModelScale", 1f);
            materialGroupName = parse.String("m_MaterialGroupName");
            lodLevel = parse.Int64("m_nLodLevel", -1);
        }

        protected override void OnEvaluate(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var name = modelName.Evaluate(ctx);

            if (string.IsNullOrEmpty(name) || name == "None")
            {
                return;
            }

            if (ctx.Result.Placements.Count >= ctx.MaxPlacements)
            {
                ctx.Warn(SmartPropDiagnosticCode.PlacementBudgetExhausted, null, "Placement budget exhausted, output is truncated");
                return;
            }

            var scale = modelScale.Evaluate(ctx);
            var uniformScale = uniformModelScale.Evaluate(ctx);
            var materialGroup = materialGroupName.Evaluate(ctx);

            if (string.IsNullOrEmpty(materialGroup))
            {
                materialGroup = null;
            }

            ctx.Result.Placements.Add(new SmartPropPlacement
            {
                ModelName = name,
                Transform = state.Transform,
                Scale = state.Scale * scale * uniformScale,
                TintColor = state.Tint,
                MaterialGroupName = materialGroup,
                LodLevel = lodLevel,
                MaterialOverrides = state.MaterialOverrides,
            });
        }
    }
}
