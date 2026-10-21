using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace KimodoUnityBridge.Command
{
    internal static class CommandInputSchemaValidator
    {
        public static bool TryValidate(JObject schema, JToken value, out string error)
        {
            if (!ValidateFiniteNumbers(value, "$", out error)) return false;
            error = ValidateNode(schema, value, "$", out string validationError) ? null : validationError;
            return error == null;
        }

        private static bool ValidateFiniteNumbers(JToken value, string path, out string error)
        {
            error = null;
            if (value == null) return true;
            if (value.Type == JTokenType.Float)
            {
                double number = value.Value<double>();
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    error = $"{path} must be a finite number.";
                    return false;
                }
            }

            if (value is JObject objectValue)
            {
                foreach (JProperty property in objectValue.Properties())
                {
                    if (!ValidateFiniteNumbers(property.Value, path + "." + property.Name, out error)) return false;
                }
            }
            else if (value is JArray arrayValue)
            {
                for (int i = 0; i < arrayValue.Count; i++)
                {
                    if (!ValidateFiniteNumbers(arrayValue[i], $"{path}[{i}]", out error)) return false;
                }
            }
            return true;
        }

        private static bool ValidateNode(JObject schema, JToken value, string path, out string error)
        {
            error = null;
            if (schema == null || value == null) return true;

            if (schema["type"] is JValue typeToken && typeToken.Type == JTokenType.String &&
                !MatchesType(value, typeToken.Value<string>()))
            {
                error = $"{path} must be {typeToken.Value<string>()}.";
                return false;
            }

            if (schema["enum"] is JArray allowedValues && !allowedValues.AnyTokenMatches(value))
            {
                error = $"{path} must be one of the declared enum values.";
                return false;
            }

            if (value is JObject objectValue)
            {
                if (schema["required"] is JArray required)
                {
                    foreach (JToken requiredName in required)
                    {
                        string name = requiredName.Value<string>();
                        if (!string.IsNullOrEmpty(name) && objectValue.Property(name) == null)
                        {
                            error = $"{path}.{name} is required.";
                            return false;
                        }
                    }
                }

                if (!WithinCount(objectValue.Count, schema["minProperties"], null, path, "properties", out error))
                {
                    return false;
                }

                JObject properties = schema["properties"] as JObject;
                JToken additionalProperties = schema["additionalProperties"];
                foreach (JProperty property in objectValue.Properties())
                {
                    JToken propertySchema = properties?[property.Name];
                    if (propertySchema is JObject knownSchema)
                    {
                        if (!ValidateNode(knownSchema, property.Value, path + "." + property.Name, out error)) return false;
                    }
                    else if (additionalProperties is JValue allowAdditional &&
                             allowAdditional.Type == JTokenType.Boolean && !allowAdditional.Value<bool>())
                    {
                        error = $"{path}.{property.Name} is not allowed.";
                        return false;
                    }
                    else if (additionalProperties is JObject additionalSchema &&
                             !ValidateNode(additionalSchema, property.Value, path + "." + property.Name, out error))
                    {
                        return false;
                    }
                }
            }

            if (value is JArray arrayValue)
            {
                if (!WithinCount(arrayValue.Count, schema["minItems"], schema["maxItems"], path, "items", out error))
                {
                    return false;
                }

                if (schema["uniqueItems"]?.Value<bool?>() == true)
                {
                    for (int i = 0; i < arrayValue.Count; i++)
                    {
                        for (int j = 0; j < i; j++)
                        {
                            if (JToken.DeepEquals(arrayValue[i], arrayValue[j]))
                            {
                                error = $"{path}[{i}] duplicates an earlier item; items must be unique.";
                                return false;
                            }
                        }
                    }
                }

                if (schema["items"] is JObject itemSchema)
                {
                    for (int i = 0; i < arrayValue.Count; i++)
                    {
                        if (!ValidateNode(itemSchema, arrayValue[i], $"{path}[{i}]", out error)) return false;
                    }
                }
            }

            if (IsNumber(value) && !WithinNumber(value, schema["minimum"], schema["maximum"], path, out error))
            {
                return false;
            }

            if (!ValidateAlternatives(schema, value, path, "anyOf", requireExactlyOne: false, out error) ||
                !ValidateAlternatives(schema, value, path, "oneOf", requireExactlyOne: true, out error))
            {
                return false;
            }

            if (schema["not"] is JObject forbiddenSchema && ValidateNode(forbiddenSchema, value, path, out _))
            {
                error = $"{path} matches a disallowed input form.";
                return false;
            }

            return true;
        }

        private static bool ValidateAlternatives(
            JObject schema,
            JToken value,
            string path,
            string keyword,
            bool requireExactlyOne,
            out string error)
        {
            error = null;
            if (!(schema[keyword] is JArray alternatives)) return true;

            int matches = 0;
            foreach (JToken alternative in alternatives)
            {
                if (alternative is JObject alternativeSchema && ValidateNode(alternativeSchema, value, path, out _))
                {
                    matches++;
                }
            }

            bool valid = requireExactlyOne ? matches == 1 : matches > 0;
            if (!valid)
            {
                error = requireExactlyOne
                    ? $"{path} must match exactly one supported input form."
                    : $"{path} does not match any supported input form.";
            }
            return valid;
        }

        private static bool WithinCount(int count, JToken minimumToken, JToken maximumToken, string path, string unit, out string error)
        {
            error = null;
            int? minimum = minimumToken?.Value<int?>();
            int? maximum = maximumToken?.Value<int?>();
            if (minimum.HasValue && count < minimum.Value)
            {
                error = $"{path} must contain at least {minimum.Value} {unit}.";
                return false;
            }
            if (maximum.HasValue && count > maximum.Value)
            {
                error = $"{path} must contain at most {maximum.Value} {unit}.";
                return false;
            }
            return true;
        }

        private static bool WithinNumber(JToken value, JToken minimumToken, JToken maximumToken, string path, out string error)
        {
            error = null;
            double number = value.Value<double>();
            if (minimumToken != null && number < minimumToken.Value<double>())
            {
                error = $"{path} must be at least {minimumToken.Value<double>().ToString(CultureInfo.InvariantCulture)}.";
                return false;
            }
            if (maximumToken != null && number > maximumToken.Value<double>())
            {
                error = $"{path} must be at most {maximumToken.Value<double>().ToString(CultureInfo.InvariantCulture)}.";
                return false;
            }
            return true;
        }

        private static bool MatchesType(JToken value, string type)
        {
            switch (type)
            {
                case "object": return value is JObject;
                case "array": return value is JArray;
                case "string": return value.Type == JTokenType.String;
                case "boolean": return value.Type == JTokenType.Boolean;
                case "null": return value.Type == JTokenType.Null;
                case "integer": return IsInteger(value);
                case "number": return IsNumber(value);
                default: return false;
            }
        }

        private static bool IsNumber(JToken value)
        {
            return value != null && (value.Type == JTokenType.Integer || value.Type == JTokenType.Float);
        }

        private static bool IsInteger(JToken value)
        {
            if (!IsNumber(value)) return false;
            if (value.Type == JTokenType.Integer) return true;
            double number = value.Value<double>();
            return !double.IsNaN(number) && !double.IsInfinity(number) && Math.Truncate(number) == number;
        }
    }

    internal static class CommandInputSchemaTokenExtensions
    {
        public static bool AnyTokenMatches(this JArray values, JToken value)
        {
            foreach (JToken candidate in values)
            {
                if (JToken.DeepEquals(candidate, value)) return true;
            }
            return false;
        }
    }
}
