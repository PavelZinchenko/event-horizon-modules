namespace GameDiagnostics
{
	public class UnityLogger : ILogger
	{
        public static readonly ILogger Instance = new UnityLogger();

        public void Log(string message, object context) => UnityEngine.Debug.Log(message, context as UnityEngine.Object);
		public void LogWarning(string message, object context) => UnityEngine.Debug.LogWarning(message, context as UnityEngine.Object);
		public void LogError(string message, object context) => UnityEngine.Debug.LogError(message, context as UnityEngine.Object);
		public void LogException(System.Exception e, object context) => UnityEngine.Debug.LogException(e, context as UnityEngine.Object);
	}

	public static class UnityLoggerBootstrap
	{
		[UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
#if UNITY_EDITOR
		[UnityEditor.InitializeOnLoadMethod]
#endif
		private static void Install() => Trace.DefaultLogger = UnityLogger.Instance;
	}
}
