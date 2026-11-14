namespace Moonlight.Api.Common;

// Collects field errors and throws one VALIDATION_FAILED with all of them.
public sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = [];

    public ValidationErrors Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }

        list.Add(message);
        return this;
    }

    public ValidationErrors Require(bool condition, string field, string message) => condition ? this : Add(field, message);

    public ValidationErrors RequireText(string? value, string field, int maxLength, bool required = true)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return required ? Add(field, $"{field} is required.") : this;
        }

        return value.Trim().Length > maxLength ? Add(field, $"{field} must be at most {maxLength} characters.") : this;
    }

    public void ThrowIfAny()
    {
        if (_errors.Count > 0)
        {
            throw new ApiException(ErrorCodes.ValidationFailed, errors: _errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()));
        }
    }
}
