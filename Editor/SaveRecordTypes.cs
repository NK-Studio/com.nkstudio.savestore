using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;

namespace SaveStore.Editor
{
    /// <summary>
    /// 프로젝트의 <c>SaveDomain&lt;TRecord&gt;</c> 구현을 찾아 "도메인 키 → 레코드 타입" 표를 만든다 — 세이브 뷰어가 blob 을 읽을 타입을 고를 때 쓴다.
    /// 키는 생성자를 부르지 않은 인스턴스에서 읽으므로 <c>Key =&gt; "wallet"</c> 처럼 상수인 도메인만 잡힌다(생성자 인자로 키를 받는 도메인은 빠진다).
    /// </summary>
    public static class SaveRecordTypes
    {
        public static Dictionary<string, Type> BuildKeyToRecordTypeMap()
        {
            var map = new Dictionary<string, Type>(StringComparer.Ordinal);
            foreach (Type type in TypeCache.GetTypesDerivedFrom<SaveDomain>())
            {
                if (type.IsAbstract == true || type.ContainsGenericParameters == true)
                {
                    continue;
                }

                Type recordType = FindRecordType(type);
                string key = TryReadConstantKey(type);
                if (recordType == null || string.IsNullOrEmpty(key) == true || map.ContainsKey(key) == true)
                {
                    continue;
                }

                map.Add(key, recordType);
            }

            return map;
        }

        private static Type FindRecordType(Type domainType)
        {
            for (Type current = domainType; current != null; current = current.BaseType)
            {
                if (current.IsGenericType == true && current.GetGenericTypeDefinition() == typeof(SaveDomain<>))
                {
                    return current.GetGenericArguments()[0];
                }
            }

            return null;
        }

        private static string TryReadConstantKey(Type domainType)
        {
            try
            {
                var instance = (SaveDomain)RuntimeHelpers.GetUninitializedObject(domainType);
                return instance.Key;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 레코드 객체를 사람이 읽을 JSON 으로 바꾼다(읽기 전용 표시용 — 되돌려 읽지 않는다). 외부 JSON 패키지에 기대지 않으려고 직접 둔다.
    /// 공개 필드와 get/set 공개 속성을 따라가고, 다형 객체(Union)는 <c>"$type"</c> 에 실제 타입 이름을 적는다.
    /// </summary>
    public static class SaveRecordDumper
    {
        private const int MaxDepth = 32;

        public static string ToJson(object value)
        {
            var builder = new StringBuilder();
            Write(builder, value, 0, null);
            return builder.ToString();
        }

        private static void Write(StringBuilder builder, object value, int depth, Type declaredType)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            if (depth > MaxDepth)
            {
                builder.Append("\"(깊이 제한)\"");
                return;
            }

            Type type = value.GetType();
            switch (value)
            {
                case string text:
                    WriteString(builder, text);
                    return;
                case bool flag:
                    builder.Append(flag == true ? "true" : "false");
                    return;
                case Enum _:
                    WriteString(builder, value.ToString());
                    return;
                case IFormattable formattable when type.IsPrimitive == true || value is decimal:
                    builder.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                    return;
                case byte[] bytes:
                    WriteString(builder, $"({bytes.Length} bytes)");
                    return;
                case IDictionary dictionary:
                    WriteDictionary(builder, dictionary, depth);
                    return;
                case IEnumerable enumerable:
                    WriteArray(builder, enumerable, depth);
                    return;
            }

            WriteObject(builder, value, type, depth, declaredType != null && declaredType != type);
        }

        private static void WriteObject(StringBuilder builder, object value, Type type, int depth, bool isPolymorphic)
        {
            builder.Append("{\n");
            bool isFirst = true;
            if (isPolymorphic == true || type.BaseType != null && type.BaseType.IsAbstract == true && type.BaseType != typeof(object))
            {
                Indent(builder, depth + 1);
                builder.Append("\"$type\": ");
                WriteString(builder, type.Name);
                isFirst = false;
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                WriteMember(builder, field.Name, field.GetValue(value), field.FieldType, depth, ref isFirst);
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.CanRead == false || property.CanWrite == false || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                WriteMember(builder, property.Name, property.GetValue(value), property.PropertyType, depth, ref isFirst);
            }

            builder.Append('\n');
            Indent(builder, depth);
            builder.Append('}');
        }

        private static void WriteMember(StringBuilder builder, string name, object memberValue, Type declaredType, int depth, ref bool isFirst)
        {
            if (isFirst == false)
            {
                builder.Append(",\n");
            }

            isFirst = false;
            Indent(builder, depth + 1);
            WriteString(builder, name);
            builder.Append(": ");
            Write(builder, memberValue, depth + 1, declaredType);
        }

        private static void WriteArray(StringBuilder builder, IEnumerable enumerable, int depth)
        {
            builder.Append('[');
            bool isFirst = true;
            foreach (object item in enumerable)
            {
                builder.Append(isFirst == true ? "\n" : ",\n");
                isFirst = false;
                Indent(builder, depth + 1);
                Write(builder, item, depth + 1, null);
            }

            if (isFirst == false)
            {
                builder.Append('\n');
                Indent(builder, depth);
            }

            builder.Append(']');
        }

        private static void WriteDictionary(StringBuilder builder, IDictionary dictionary, int depth)
        {
            builder.Append('{');
            bool isFirst = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                builder.Append(isFirst == true ? "\n" : ",\n");
                isFirst = false;
                Indent(builder, depth + 1);
                WriteString(builder, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                builder.Append(": ");
                Write(builder, entry.Value, depth + 1, null);
            }

            if (isFirst == false)
            {
                builder.Append('\n');
                Indent(builder, depth);
            }

            builder.Append('}');
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            foreach (char character in text)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        private static void Indent(StringBuilder builder, int depth)
        {
            builder.Append(' ', depth * 2);
        }
    }
}
