namespace RazorC2.Utilities
{
    public static class HashHelper
    {
        public static string ShortenHash(string fullHash)
        {
            if (string.IsNullOrEmpty(fullHash))
                return "???";
            return fullHash.Substring(0, Math.Min(8, fullHash.Length));
        }
    }
}