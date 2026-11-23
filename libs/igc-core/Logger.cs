using Sandbox.ModAPI.Ingame;

namespace SpaceEngineers.Libs.IgcCore
{
    /// <summary>
    /// Simple logger that writes trace, warning, and error messages to a terminal block's CustomData.
    /// </summary>
    public class Logger
    {
        private readonly IMyTerminalBlock _logBlock;

        /// <summary>
        /// Creates a logger that writes to the given terminal block.
        /// </summary>
        /// <param name="logBlock">Terminal block whose CustomData receives log entries.</param>
        public Logger(IMyTerminalBlock logBlock)
        {
            _logBlock = logBlock;
        }

        /// <summary>Appends a trace-level message to CustomData.</summary>
        /// <param name="message">Trace message.</param>
        public void LogTrace(string message)
        {
            _logBlock.CustomData = _logBlock.CustomData + $"Trace: {message} \n";
        }

        /// <summary>Clears all log entries from CustomData.</summary>
        public void Clear()
        {
            _logBlock.CustomData = "";
        }

        /// <summary>Appends a warning-level message to CustomData.</summary>
        /// <param name="message">Warning message.</param>
        public void LogWarning(string message)
        {
            _logBlock.CustomData = _logBlock.CustomData + $"Warning: {message} \n";
        }

        /// <summary>Appends an error-level message with details to CustomData.</summary>
        /// <param name="message">Error message.</param>
        /// <param name="errorMessage">Additional error details.</param>
        public void LogError(string message, string errorMessage)
        {
            _logBlock.CustomData = _logBlock.CustomData + $"Error: {message}. Details: {errorMessage} \n";
        }
    }
}