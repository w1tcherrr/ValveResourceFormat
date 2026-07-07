namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Base for vector computations that write their result to a variable. A missing input
    /// position means the current element position.
    /// </summary>
    abstract class ComputeOperation : SmartPropOperation
    {
        protected readonly record struct VectorInput(bool Present, VectorAttribute Value, StringAttribute Space);

        private readonly string? outputVariable;
        private readonly StringAttribute outputCoordinateSpace;

        protected ComputeOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            outputVariable = parse.RawString("m_OutputVariableName");
            outputCoordinateSpace = parse.String("m_OutputCoordinateSpace");
        }

        public sealed override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            if (string.IsNullOrEmpty(outputVariable))
            {
                return true;
            }

            var outputSpace = SmartPropHelpers.ParseSpace(outputCoordinateSpace.Evaluate(ctx), SmartPropSpace.Object);
            Compute(in state, ctx, outputSpace);
            return true;
        }

        protected abstract void Compute(in SmartPropState state, SmartPropEvaluationContext ctx, SmartPropSpace outputSpace);

        protected void SetOutput(SmartPropEvaluationContext ctx, object value) => ctx.SetVariable(outputVariable!, value);

        protected static VectorInput ParseInput(SmartPropDefinitionParser parse, string field, string spaceField)
            => new(parse.Contains(field), parse.Vector(field), parse.String(spaceField));

        protected static Vector3 Point(in VectorInput input, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var space = SmartPropHelpers.ParseSpace(input.Space.Evaluate(ctx), SmartPropSpace.Object);

            return input.Present
                ? SmartPropHelpers.PointToWorld(input.Value.Evaluate(ctx), space, state)
                : state.Transform.Translation;
        }

        protected static Vector3 Direction(in VectorInput input, in SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var space = SmartPropHelpers.ParseSpace(input.Space.Evaluate(ctx), SmartPropSpace.Object);
            return SmartPropHelpers.DirectionToWorld(input.Value.Evaluate(ctx), space, state);
        }
    }

    /// <summary>
    /// Writes the distance between two points to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_ComputeDistance3D">CSmartPropOperation_ComputeDistance3D</seealso>
    sealed class ComputeDistanceOperation : ComputeOperation
    {
        private readonly VectorInput inputA;
        private readonly VectorInput inputB;

        public ComputeDistanceOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            inputA = ParseInput(parse, "m_InputPositionA", "m_CoordinateSpaceA");
            inputB = ParseInput(parse, "m_InputPositionB", "m_CoordinateSpaceB");
        }

        protected override void Compute(in SmartPropState state, SmartPropEvaluationContext ctx, SmartPropSpace outputSpace)
        {
            var a = Point(in inputA, in state, ctx);
            var b = Point(in inputB, in state, ctx);
            SetOutput(ctx, (double)Vector3.Distance(a, b));
        }
    }

    /// <summary>
    /// Writes the dot product of two directions to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_ComputeDotProduct3D">CSmartPropOperation_ComputeDotProduct3D</seealso>
    sealed class ComputeDotProductOperation : ComputeOperation
    {
        private readonly VectorInput inputA;
        private readonly VectorInput inputB;

        public ComputeDotProductOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            inputA = ParseInput(parse, "m_InputVectorA", "m_CoordinateSpaceA");
            inputB = ParseInput(parse, "m_InputVectorB", "m_CoordinateSpaceB");
        }

        protected override void Compute(in SmartPropState state, SmartPropEvaluationContext ctx, SmartPropSpace outputSpace)
        {
            var a = Direction(in inputA, in state, ctx);
            var b = Direction(in inputB, in state, ctx);
            SetOutput(ctx, (double)Vector3.Dot(a, b));
        }
    }

    /// <summary>
    /// Writes the cross product of two directions to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_ComputeCrossProduct3D">CSmartPropOperation_ComputeCrossProduct3D</seealso>
    sealed class ComputeCrossProductOperation : ComputeOperation
    {
        private readonly VectorInput inputA;
        private readonly VectorInput inputB;

        public ComputeCrossProductOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            inputA = ParseInput(parse, "m_InputVectorA", "m_CoordinateSpaceA");
            inputB = ParseInput(parse, "m_InputVectorB", "m_CoordinateSpaceB");
        }

        protected override void Compute(in SmartPropState state, SmartPropEvaluationContext ctx, SmartPropSpace outputSpace)
        {
            var a = Direction(in inputA, in state, ctx);
            var b = Direction(in inputB, in state, ctx);
            SetOutput(ctx, SmartPropHelpers.DirectionToSpace(Vector3.Cross(a, b), outputSpace, state));
        }
    }

    /// <summary>
    /// Writes the vector from one point to another to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_ComputeVectorBetweenPoints3D">CSmartPropOperation_ComputeVectorBetweenPoints3D</seealso>
    sealed class ComputeVectorBetweenPointsOperation : ComputeOperation
    {
        private readonly VectorInput inputA;
        private readonly VectorInput inputB;

        public ComputeVectorBetweenPointsOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            inputA = ParseInput(parse, "m_InputPositionA", "m_CoordinateSpaceA");
            inputB = ParseInput(parse, "m_InputPositionB", "m_CoordinateSpaceB");
        }

        protected override void Compute(in SmartPropState state, SmartPropEvaluationContext ctx, SmartPropSpace outputSpace)
        {
            var a = Point(in inputA, in state, ctx);
            var b = Point(in inputB, in state, ctx);
            SetOutput(ctx, SmartPropHelpers.DirectionToSpace(b - a, outputSpace, state));
        }
    }

    /// <summary>
    /// Writes a direction, optionally normalized, to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_ComputeNormalizedVector3D">CSmartPropOperation_ComputeNormalizedVector3D</seealso>
    sealed class ComputeNormalizedVectorOperation : ComputeOperation
    {
        private readonly VectorInput inputA;
        private readonly BoolAttribute normalized;

        public ComputeNormalizedVectorOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            inputA = ParseInput(parse, "m_InputVectorA", "m_CoordinateSpaceA");
            normalized = parse.Bool("m_bNormalized", true);
        }

        protected override void Compute(in SmartPropState state, SmartPropEvaluationContext ctx, SmartPropSpace outputSpace)
        {
            var a = Direction(in inputA, in state, ctx);
            var result = normalized.Evaluate(ctx) && a != Vector3.Zero
                ? Vector3.Normalize(a)
                : a;
            SetOutput(ctx, SmartPropHelpers.DirectionToSpace(result, outputSpace, state));
        }
    }

    /// <summary>
    /// Writes the projection of one direction onto another (or onto its plane) to a variable.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_ComputeProjectVector3D">CSmartPropOperation_ComputeProjectVector3D</seealso>
    sealed class ComputeProjectVectorOperation : ComputeOperation
    {
        private readonly VectorInput inputA;
        private readonly VectorInput inputB;
        private readonly BoolAttribute plane;

        public ComputeProjectVectorOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            inputA = ParseInput(parse, "m_InputVectorA", "m_CoordinateSpaceA");
            inputB = ParseInput(parse, "m_InputVectorB", "m_CoordinateSpaceB");
            plane = parse.Bool("m_bPlane", false);
        }

        protected override void Compute(in SmartPropState state, SmartPropEvaluationContext ctx, SmartPropSpace outputSpace)
        {
            var a = Direction(in inputA, in state, ctx);
            var b = Direction(in inputB, in state, ctx);

            if (b == Vector3.Zero)
            {
                return;
            }

            var normal = Vector3.Normalize(b);
            var projected = plane.Evaluate(ctx)
                ? a - Vector3.Dot(a, normal) * normal
                : Vector3.Dot(a, normal) * normal;

            SetOutput(ctx, SmartPropHelpers.DirectionToSpace(projected, outputSpace, state));
        }
    }
}
