﻿using System;
using System.Collections.Generic;

namespace Vostok.Logging.Logs
{
    public class ConsoleLog : ILog
    {
        private static readonly Dictionary<LogLevel, ConsoleColor> LevelToColor = new Dictionary<LogLevel, ConsoleColor>
        {
            {LogLevel.Trace, ConsoleColor.Gray},
            {LogLevel.Debug, ConsoleColor.Gray},
            {LogLevel.Info, ConsoleColor.White},
            {LogLevel.Warn, ConsoleColor.Yellow},
            {LogLevel.Error, ConsoleColor.Red},
            {LogLevel.Fatal, ConsoleColor.Red}
        };

        private readonly object syncLock = new object();

        private readonly LogLevel minLevel;

        public ConsoleLog(LogLevel minLevel = LogLevel.Trace)
        {
            this.minLevel = minLevel;
        }

        public void Log(LogEvent logEvent)
        {
            if (logEvent.Level < minLevel)
                return;

            lock (syncLock)
            {
                var oldColor = Console.ForegroundColor;
                Console.ForegroundColor = LevelToColor[logEvent.Level];
                Console.Out.Write(LogEventFormatter.Format(logEvent));
                Console.ForegroundColor = oldColor;
            }
        }

        public bool IsEnabledFor(LogLevel level)
        {
            return level >= minLevel;
        }
    }
}