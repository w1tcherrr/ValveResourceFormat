using System.Threading.Tasks;
using ValveResourceFormat.Renderer.SceneNodes;
using ValveResourceFormat.ResourceTypes;

namespace Tests.Renderer
{
    /// <summary>
    /// Covers how a model picks its LoD level at render time: the screen-size metric, and the level that
    /// metric selects. The scene node itself needs a GL context, so the metric is tested through the pure
    /// functions it delegates to.
    /// </summary>
    public class ModelLodSelectionTest
    {
        // A 1080p viewport with a 90 degree vertical field of view, where M22 is 1 / tan(45 deg) = 1.
        private const float WindowHeight = 1080f;
        private const float SquareFov = 1f;

        [Test]
        public async Task MetricGrowsAsTheModelGetsSmallerOnScreen()
        {
            var near = ModelSceneNode.ComputeLodMetric(100f, 1f, WindowHeight, SquareFov);
            var far = ModelSceneNode.ComputeLodMetric(400f, 1f, WindowHeight, SquareFov);

            using (Assert.Multiple())
            {
                // A unit sphere 100 units away covers 1080 * 1 * 1 / 100 = 10.8 px, so the metric is 100/10.8.
                await Assert.That(near).IsEqualTo(100f / 10.8f).Within(0.0001f);

                // Four times the distance is a quarter the size, so four times the metric.
                await Assert.That(far).IsEqualTo(near * 4f).Within(0.001f);
                await Assert.That(far).IsGreaterThan(near);

                // Scaling the model up is the same as moving it closer.
                await Assert.That(ModelSceneNode.ComputeLodMetric(400f, 4f, WindowHeight, SquareFov))
                    .IsEqualTo(near).Within(0.001f);

                // A taller viewport draws the model bigger, so it stays on a more detailed level.
                await Assert.That(ModelSceneNode.ComputeLodMetric(100f, 1f, WindowHeight * 2f, SquareFov))
                    .IsLessThan(near);
            }
        }

        [Test]
        public async Task ModelAtOrBehindTheCameraTakesTheMostDetailedLevel()
        {
            var lod = new ModelLodInfo([1, 2, 4], [0f, 35f, 50f]);

            using (Assert.Multiple())
            {
                // Zero or negative distance treats the model as covering the whole screen, which lands
                // below every switch threshold and so selects the most detailed level.
                await Assert.That(lod.SelectLevel(ModelSceneNode.ComputeLodMetric(0f, 1f, WindowHeight, SquareFov))).IsZero();
                await Assert.That(lod.SelectLevel(ModelSceneNode.ComputeLodMetric(-10f, 1f, WindowHeight, SquareFov))).IsZero();

                // A model scaled to nothing covers no pixels, and takes the same lowest metric.
                await Assert.That(ModelSceneNode.ComputeLodMetric(100f, 0f, WindowHeight, SquareFov)).IsZero();
                await Assert.That(lod.SelectLevel(ModelSceneNode.ComputeLodMetric(100f, 0f, WindowHeight, SquareFov))).IsZero();
            }
        }

        /// <summary>
        /// How big a model draws depends on the largest axis it was scaled along, whichever that is.
        /// </summary>
        [Test]
        public async Task ScaleIsTakenFromTheLargestAxis()
        {
            using (Assert.Multiple())
            {
                await Assert.That(ModelSceneNode.GetLargestAxisScale(Matrix4x4.Identity)).IsEqualTo(1f).Within(0.0001f);
                await Assert.That(ModelSceneNode.GetLargestAxisScale(Matrix4x4.CreateScale(3f))).IsEqualTo(3f).Within(0.0001f);
                await Assert.That(ModelSceneNode.GetLargestAxisScale(Matrix4x4.CreateScale(1f, 5f, 2f))).IsEqualTo(5f).Within(0.0001f);

                // Rotation is not scale, and a rotated non-uniform scale still reports its largest axis.
                await Assert.That(ModelSceneNode.GetLargestAxisScale(Matrix4x4.CreateRotationZ(0.7f))).IsEqualTo(1f).Within(0.0001f);
                await Assert.That(ModelSceneNode.GetLargestAxisScale(
                    Matrix4x4.CreateScale(1f, 5f, 2f) * Matrix4x4.CreateRotationY(1.1f))).IsEqualTo(5f).Within(0.0001f);

                // Translation is not scale either.
                await Assert.That(ModelSceneNode.GetLargestAxisScale(Matrix4x4.CreateTranslation(50f, 0f, 0f)))
                    .IsEqualTo(1f).Within(0.0001f);
            }
        }

        /// <summary>
        /// The metric and the level table meet here: a model walking away from the camera has to cross the
        /// switch thresholds in order, and a scaled-up model crosses them later.
        /// </summary>
        [Test]
        public async Task MetricSelectsLevelsInOrderAsTheModelRecedes()
        {
            // Three levels, one mesh each, switching at metric 0, 35 and 50.
            var lod = new ModelLodInfo([1, 2, 4], [0f, 35f, 50f]);

            int LevelAt(float distance, float scale = 1f)
                => lod.SelectLevel(ModelSceneNode.ComputeLodMetric(distance, scale, WindowHeight, SquareFov));

            using (Assert.Multiple())
            {
                await Assert.That(LevelAt(10f)).IsZero();
                await Assert.That(LevelAt(300f)).IsZero();
                await Assert.That(LevelAt(400f)).IsEqualTo(1);
                await Assert.That(LevelAt(600f)).IsEqualTo(2);
                await Assert.That(LevelAt(10000f)).IsEqualTo(2);

                // The level never goes back down as the model recedes further.
                var previous = -1;
                for (var distance = 1f; distance < 5000f; distance *= 1.2f)
                {
                    var level = LevelAt(distance);
                    await Assert.That(level).IsGreaterThanOrEqualTo(previous);
                    previous = level;
                }

                // A model scaled four times larger stays on level 0 four times further out.
                await Assert.That(LevelAt(400f, 4f)).IsZero();
                await Assert.That(LevelAt(1600f, 4f)).IsEqualTo(1);
            }
        }
    }
}
