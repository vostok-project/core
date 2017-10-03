﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Vostok.Commons
{
    public static class UrlExtensions
    {
        public static string ToString(this Uri url, bool includeQuery)
        {
            var urlString = url.ToString();

            if (!includeQuery)
            {
                var queryBeginning = urlString.IndexOf("?", StringComparison.Ordinal);
                if (queryBeginning >= 0)
                    urlString = urlString.Substring(0, queryBeginning);
            }

            return urlString;
        }

        public static string Normalize(this Uri url)
        {
            const char separator = '/';

            var urlString = url.ToString(false);
            var stringBuilder = new StringBuilder(urlString.Length);
            var segmentBeginning = 0;
            for (var i = 0; i < urlString.Length; i++)
            {
                if (urlString[i] == separator)
                {
                    var segmentLength = i - segmentBeginning;
                    AppendSegment(stringBuilder, urlString, segmentBeginning, segmentLength);
                    stringBuilder.Append(separator);
                    segmentBeginning = i + 1;
                }
            }

            AppendSegment(stringBuilder, urlString, segmentBeginning, urlString.Length - segmentBeginning);
            return stringBuilder.ToString();
        }

        private static void AppendSegment(StringBuilder stringBuilder, string str, int offset, int length)
        {
            if (length <= 0)
            {
                return;
            }

            if (CheckSegment(str, offset, length, out var sergmentName))
            {
                stringBuilder.Append("{" + sergmentName + "}");
            }
            else
            {
                AppendSubstring(stringBuilder, str, offset, length);
            }
        }

        private static void AppendSubstring(StringBuilder stringBuilder, string str, int offset, int segmentLength)
        {
            for (var i = 0; i < segmentLength; i++)
            {
                stringBuilder.Append(str[offset + i]);
            }
        }

        #region segment checkers
        private static readonly IDictionary<string, Func<string, int, int, bool>> segementCheckers = new Dictionary<string, Func<string, int, int, bool>>
        {
            ["guid"] = IsGuidSegment,
            ["num"] = IsNumericalSegment,
            ["enc"] = IsUrlEncodedSegment,
            ["hex"] = IsLongHexSegment
        };

        private static bool CheckSegment(string str, int offset, int length, out string segmentName)
        {
            foreach (var checker in segementCheckers)
            {
                if (checker.Value(str, offset, length))
                {
                    segmentName = checker.Key;
                    return true;
                }
            }

            segmentName = null;
            return false;
        }

        private static bool IsLongHexSegment(string str, int offset, int length)
        {
            if (length < 8)
                return false;

            if (length % 2 != 0)
                return false;

            for (var i = 0; i < length; i++)
            {
                if (!IsValidHexChar(str[offset + i]))
                    return false;
            }

            return true;
        }

        private static bool IsUrlEncodedSegment(string str, int offset, int length)
        {
            for (var i = 0; i < length; i++)
            {
                var character = str[offset + i];
                var isPercent = character == '%';
                var isPlus = character == '+';
                if (isPlus || isPercent)
                    return true;
            }

            return false;
        }

        private static bool IsNumericalSegment(string str, int offset, int length)
        {
            for (var i = 0; i < length; i++)
            {
                var character = str[offset + i];
                var isDigit = character >= '0' && character <= '9';
                var isDash = character == '-';
                if (!isDash && !isDigit)
                    return false;
            }

            return true;
        }

        private static bool IsGuidSegment(string str, int offset, int length)
        {
            if (length != 36)
                return false;

            for (var i = 0; i < guidDashPositions.Length; i++)
            {
                if (str[offset + guidDashPositions[i]] != '-')
                    return false;
            }

            for (var i = 0; i < guidHexPositions.Length; i++)
            {
                if (!IsValidHexChar(str[offset + guidHexPositions[i]]))
                    return false;
            }

            return true;
        }

        private static readonly int[] guidDashPositions = { 8, 13, 18, 23 };
        private static readonly int[] guidHexPositions = { 0, 1, 2, 3, 4, 5, 6, 7, 9, 10, 11, 12, 14, 15, 16, 17, 19, 20, 21, 22, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35 };
        #endregion

        private static bool IsValidHexChar(char symbol)
        {
            return symbol >= '0' && symbol <= '9'
                   || symbol >= 'a' && symbol <= 'f'
                   || symbol >= 'A' && symbol <= 'F';
        }
    }
}