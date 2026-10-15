using System;

namespace Utilites
{
    // Engine-free replacement for UnityEngine.Mathf. Results match Mathf exactly.
    public static class MathEx
    {
        public const float PI = (float)Math.PI;
        public const float Deg2Rad = PI * 2f / 360f;
        public const float Rad2Deg = 1f / Deg2Rad;

        // Unlike System.Math.Clamp, never throws when min > max.
        public static float Clamp(float value, float min, float max)
        {
            if (value < min) value = min;
            else if (value > max) value = max;
            return value;
        }

        public static int Clamp(int value, int min, int max)
        {
            if (value < min) value = min;
            else if (value > max) value = max;
            return value;
        }

        public static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        // Rounds half to even, like Mathf.RoundToInt.
        public static int RoundToInt(float value) => (int)Math.Round(value);

        public static float Min(float a, float b) => a < b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;

        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Sqrt(float value) => (float)Math.Sqrt(value);

        public static bool Approximately(float a, float b) =>
            Math.Abs(b - a) < Max(1E-06f * Max(Math.Abs(a), Math.Abs(b)), float.Epsilon * 8);
    }
}
