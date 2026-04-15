namespace SimHub.Logging
{
    /// <summary>Minimal stub so source files that log don't need the real SimHub DLL.</summary>
    public static class Current
    {
        public static void Debug(string message) { }
        public static void Info(string message) { }
        public static void Warn(string message) { }
        public static void Error(string message) { }
    }
}
