namespace ValveResourceFormat.ResourceTypes.SmartProps.Operations
{
    /// <summary>
    /// Zeroes the selected euler components (pitch, yaw, roll) of the current rotation.
    /// </summary>
    /// <seealso href="https://s2v.app/SchemaExplorer/cs2/smartprops/CSmartPropOperation_ResetRotation">CSmartPropOperation_ResetRotation</seealso>
    sealed class ResetRotationOperation : SmartPropOperation
    {
        private readonly BoolAttribute resetPitch;
        private readonly BoolAttribute resetYaw;
        private readonly BoolAttribute resetRoll;

        public ResetRotationOperation(SmartPropDefinitionParser parse) : base(parse)
        {
            resetPitch = parse.Bool("m_bResetPitch", true);
            resetYaw = parse.Bool("m_bResetYaw", true);
            resetRoll = parse.Bool("m_bResetRoll", true);
        }

        public override bool Apply(ref SmartPropState state, SmartPropEvaluationContext ctx)
        {
            var zeroPitch = resetPitch.Evaluate(ctx);
            var zeroYaw = resetYaw.Evaluate(ctx);
            var zeroRoll = resetRoll.Evaluate(ctx);

            var forward = Vector3.TransformNormal(Vector3.UnitX, state.Transform);
            var left = Vector3.TransformNormal(Vector3.UnitY, state.Transform);
            var up = Vector3.TransformNormal(Vector3.UnitZ, state.Transform);

            var yaw = float.RadiansToDegrees(MathF.Atan2(forward.Y, forward.X));
            var pitch = float.RadiansToDegrees(MathF.Atan2(-forward.Z, MathF.Sqrt(forward.X * forward.X + forward.Y * forward.Y)));
            var roll = float.RadiansToDegrees(MathF.Atan2(left.Z, up.Z));

            var angles = new Vector3(zeroPitch ? 0f : pitch, zeroYaw ? 0f : yaw, zeroRoll ? 0f : roll);
            var rotation = EntityTransformHelper.CreateRotationMatrixFromEulerAngles(angles);
            rotation.Translation = state.Transform.Translation;
            state.Transform = rotation;
            return true;
        }
    }
}
