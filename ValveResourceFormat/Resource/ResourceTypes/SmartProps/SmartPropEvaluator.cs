using ValveKeyValue;

namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// One-shot evaluation of a smart prop document. Prefer <see cref="SmartPropDocument"/>
    /// when evaluating the same document repeatedly.
    /// </summary>
    public static class SmartPropEvaluator
    {
        /// <summary>
        /// Loads and evaluates a smart prop resource in one step.
        /// </summary>
        public static SmartPropEvaluationResult Evaluate(SmartProp smartProp, SmartPropEvaluationOptions options)
            => SmartPropDocument.Load(smartProp).Evaluate(options);

        /// <summary>
        /// Loads and evaluates a <c>CSmartPropRoot</c> KV3 tree in one step.
        /// </summary>
        public static SmartPropEvaluationResult Evaluate(KVObject root, SmartPropEvaluationOptions options)
            => SmartPropDocument.Load(root).Evaluate(options);
    }
}
