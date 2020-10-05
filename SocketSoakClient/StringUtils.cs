using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SocketSoakClient
{
    internal static class StringExtensions
    {
        public static bool IsNotEmpty(this string text)
        {
            return String.IsNullOrEmpty(text) == false;
        }

        public static bool IsEmpty(this string text)
        {
            return String.IsNullOrEmpty(text);
        }

        public static bool IsJson(this string input)
        {
            if (IsEmpty(input))
            {
                return false;
            }

            input = input.Trim();
            return (input.StartsWith("{") && input.EndsWith("}"))
                   || (input.StartsWith("[") && input.EndsWith("]"));
        }

        public static string SafeTrim(this string input)
        {
            if (input.IsEmpty())
            {
                return input;
            }

            return input.Trim();
        }

        public static string JoinStrings(this IEnumerable<string> input, string delimiter = ", ")
        {
            if (input == null)
            {
                return String.Empty;
            }

            return string.Join(delimiter, input.Where(IsNotEmpty));
        }

        public static string Join<T>(this IEnumerable<T> listOfTs, Func<T, string> selector, string delimiter = ",") where T : class
        {
            if (listOfTs != null)
            {
                return string.Join(delimiter, listOfTs.Select(selector));
            }

            return String.Empty;
        }

        public static bool EqualsTo(this string input, string other, bool caseSensitive = false)
        {
            return string.Equals(input, other, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }

        public static byte[] ToByteArray(this string hex)
        {
            int numberChars = hex.Length;
            byte[] bytes = new byte[numberChars / 2];
            for (int i = 0; i < numberChars; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }

            return bytes;
        }
    }

    /// <summary>
    /// String utility functions.
    /// </summary>
    public static class StringUtils
    {
        /// <summary>
        /// Returns UTF8 bytes from a given string.
        /// </summary>
        /// <param name="text">input string.</param>
        /// <returns>UTF8 byte[].</returns>
        public static byte[] GetBytes(this string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        internal static string GetText(this byte[] bytes)
        {
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        internal static string ToBase64(this byte[] bytes)
        {
            return Convert.ToBase64String(bytes);
        }

        internal static string ToBase64(this string text)
        {
            if (text.IsEmpty())
            {
                return string.Empty;
            }

            return text.GetBytes().ToBase64();
        }

        // https://brockallen.com/2014/10/17/base64url-encoding/
        internal static byte[] FromBase64(this string base64String)
        {
            if (base64String.IsEmpty())
            {
                return new byte[0];
            }

            string s = base64String;
            s = s.Replace('-', '+'); // 62nd char of encoding
            s = s.Replace('_', '/'); // 63rd char of encoding

            switch (s.Length % 4)
            {
                // Pad with trailing '='s
                case 0: break; // No pad chars in this case
                case 2: s += "=="; break; // Two pad chars
                case 3: s += "="; break; // One pad char
                default: throw new Exception("Illegal base64url string!");
            }

            return Convert.FromBase64String(s);
        }

        internal static string EncodeUriPart(this string text)
        {
            return Uri.EscapeDataString(text);
        }
    }
}