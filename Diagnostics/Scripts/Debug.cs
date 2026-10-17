namespace GameDiagnostics
{
	public interface ILogger
	{
		void Log(string message, object context = null);
		void LogWarning(string message, object context = null);
		void LogError(string message, object context = null);
		void LogException(System.Exception e, object context = null);
	}

	public class ConsoleLogger : ILogger
	{
		public static readonly ILogger Instance = new ConsoleLogger();

		public void Log(string message, object context) => System.Console.Out.WriteLine(message);
		public void LogWarning(string message, object context) => System.Console.Out.WriteLine("Warning: " + message);
		public void LogError(string message, object context) => System.Console.Error.WriteLine("Error: " + message);
		public void LogException(System.Exception e, object context) => System.Console.Error.WriteLine(e);
	}

	public static class Debug
	{
		private static ILogger Logger => Trace.DefaultLogger;

		[System.Diagnostics.Conditional("UNITY_EDITOR")]
		public static void Log(string message, object context = null) => Logger.Log(message, context);

		[System.Diagnostics.Conditional("UNITY_EDITOR")]
		public static void LogWarning(string message, object context = null) => Logger.LogWarning(message, context);

		[System.Diagnostics.Conditional("UNITY_EDITOR")]
		public static void LogError(string message, object context = null) => Logger.LogError(message, context);

		[System.Diagnostics.Conditional("UNITY_EDITOR")]
		public static void LogException(System.Exception e, object context = null) => Logger.LogException(e, context);
	}

	public static class Trace
	{
        private static ILogger _logger;
        private static ILogger _defaultLogger = ConsoleLogger.Instance;

        public static ILogger Logger { get => _logger ?? _defaultLogger; set => _logger = value; }
        public static ILogger DefaultLogger { get => _defaultLogger; set => _defaultLogger = value ?? ConsoleLogger.Instance; }

        public static void Log(string message) => Logger.Log(message);
		public static void LogWarning(string message) => Logger.LogWarning(message);
		public static void LogError(string message) => Logger.LogError(message);
		public static void LogException(System.Exception e) => Logger.LogException(e);
	}
}
