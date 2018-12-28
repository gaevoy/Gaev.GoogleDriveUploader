using System;
using Google.Apis.Logging;
using Serilog.Events;

namespace Gaev.GoogleDriveUploader
{
    public class SerilogLogger : ILogger
    {
        private readonly Serilog.ILogger _logger;

        public SerilogLogger(Serilog.ILogger logger)
        {
            _logger = logger;
        }

        public ILogger ForType(Type type)
        {
            return new SerilogLogger(_logger.ForContext(type));
        }

        public ILogger ForType<T>()
        {
            return new SerilogLogger(_logger.ForContext<T>());
        }

        public void Debug(string message, params object[] formatArgs)
        {
            _logger.Debug(message, formatArgs);
        }

        public void Info(string message, params object[] formatArgs)
        {
            _logger.Information(message, formatArgs);
        }

        public void Warning(string message, params object[] formatArgs)
        {
            if (message == "Add parameter should not get null values. type={0}, name={1}")
                return; // Ignore warning here https://github.com/googleapis/google-api-dotnet-client/blob/2df445ea9fce00868cf0a1ea010cb40ef6ed64a8/Src/Support/Google.Apis/Upload/ResumableUpload.cs#L934
            _logger.Warning(message, formatArgs);
        }

        public void Error(Exception exception, string message, params object[] formatArgs)
        {
            _logger.Error(exception, message, formatArgs);
        }

        public void Error(string message, params object[] formatArgs)
        {
            _logger.Error(message, formatArgs);
        }

        public bool IsDebugEnabled => _logger.IsEnabled(LogEventLevel.Debug);
    }
}