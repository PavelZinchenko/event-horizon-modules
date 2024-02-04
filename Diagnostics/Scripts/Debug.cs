namespace GameDiagnostics
{
	public interface ILogger
	{
		void Log(string message, UnityEngine.GameObject context = null);
		void LogWarning(string message, UnityEngine.GameObject context = null);
		void LogError(string message, UnityEngine.GameObject context = null);
		void LogException(System.Exception e, UnityEngine.GameObject context = null);
	}

	public static class Debug
	{
		private static ILogger _logger = new UnityLogger();

		[System.Diagnostics.Conditional("UNITY_EDITOR")]
		public static void Log(string message, UnityEngine.GameObject context = null) => _logger.Log(message, context);

		[System.Diagnostics.Conditional("UNITY_EDITOR")]
		public static void LogWarning(string message, UnityEngine.GameObject context = null) => _logger.LogWarning(message, context);

		[System.Diagnostics.Conditional("UNITY_EDITOR")]
		public static void LogError(string message, UnityEngine.GameObject context = null) => _logger.LogError(message, context);

		[System.Diagnostics.Conditional("UNITY_EDITOR")]
		public static void LogException(System.Exception e, UnityEngine.GameObject context = null) => _logger.LogException(e, context);
	}

	public static class Trace
	{
		private static ILogger _logger = new UnityLogger();

		public static void Log(string message) => _logger.Log(message);
		public static void LogWarning(string message) => _logger.LogWarning(message);
		public static void LogError(string message) => _logger.LogError(message);
		public static void LogException(System.Exception e) => _logger.LogException(e);
	}
}
