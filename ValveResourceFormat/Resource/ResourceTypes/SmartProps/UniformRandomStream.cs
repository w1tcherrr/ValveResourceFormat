namespace ValveResourceFormat.ResourceTypes.SmartProps
{
    /// <summary>
    /// Uniform pseudo-random number stream matching the Source engine generator,
    /// so that smart prop evaluation is deterministic for a given seed.
    /// </summary>
    internal sealed class UniformRandomStream
    {
        private const int IA = 16807;
        private const int IM = 2147483647;
        private const int IQ = 127773;
        private const int IR = 2836;
        private const int NTAB = 32;
        private const int NDIV = 1 + (IM - 1) / NTAB;
        private const int MaxRandomRange = 0x7FFFFFFF;
        private const double AM = 1.0 / IM;
        private const double EPS = 1.2e-7;
        private const double RNMX = 1.0 - EPS;

        private readonly int[] iv = new int[NTAB];
        private int idum;
        private int iy;

        public UniformRandomStream(int seed)
        {
            SetSeed(seed);
        }

        public void SetSeed(int seed)
        {
            idum = seed < 0 ? seed : -seed;
            iy = 0;
        }

        private int GenerateRandomNumber()
        {
            if (idum <= 0 || iy == 0)
            {
                idum = -idum < 1 ? 1 : -idum;

                for (var j = NTAB + 7; j >= 0; j--)
                {
                    var k1 = idum / IQ;
                    idum = IA * (idum - k1 * IQ) - IR * k1;

                    if (idum < 0)
                    {
                        idum += IM;
                    }

                    if (j < NTAB)
                    {
                        iv[j] = idum;
                    }
                }

                iy = iv[0];
            }

            var k = idum / IQ;
            idum = IA * (idum - k * IQ) - IR * k;

            if (idum < 0)
            {
                idum += IM;
            }

            var j2 = iy / NDIV;
            iy = iv[j2];
            iv[j2] = idum;

            return iy;
        }

        public float RandomFloat(float low = 0f, float high = 1f)
        {
            // Rounding to float before the clamp matches the original single-precision math
            var fl = (float)(AM * GenerateRandomNumber());

            if (fl > RNMX)
            {
                fl = (float)RNMX;
            }

            return fl * (high - low) + low;
        }

        public int RandomInt(int low, int high)
        {
            if (high < low)
            {
                return low;
            }

            var x = unchecked((uint)(high - low + 1));

            if (x <= 1)
            {
                return low;
            }

            var maxAcceptable = MaxRandomRange - (int)(((uint)MaxRandomRange + 1) % x);
            int n;

            do
            {
                n = GenerateRandomNumber();
            }
            while (n > maxAcceptable);

            return low + (int)(n % x);
        }
    }
}
