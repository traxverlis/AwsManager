namespace AwsManager.Services;

public static class ResourceValidation
{
    public static void Capacity(int minimum, int desired, int maximum)
    {
        if (minimum < 0 || minimum > desired || desired > maximum)
            throw new ArgumentException("Capacites invalides : 0 <= minimum <= souhaite <= maximum.");
    }
    public static void Tags(IEnumerable<(string Key, string Value)> tags)
    {
        var values = tags.ToList();
        if (values.Count > 50) throw new ArgumentException("Maximum 50 tags utilisateur.");
        if (values.Any(tag => string.IsNullOrWhiteSpace(tag.Key) || tag.Key.Length > 128 || tag.Value.Length > 256))
            throw new ArgumentException("Cle de tag requise (128 caracteres maximum), valeur de 256 caracteres maximum.");
        if (values.Select(tag => tag.Key).Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("Chaque cle de tag doit etre unique.");
    }
    public static string RelativeKey(string key, string prefix) => key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
    public static string FileSize(long bytes) => bytes < 1024 ? $"{bytes} o" : bytes < 1024 * 1024 ? $"{bytes / 1024d:N1} Ko" : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024):N1} Mo" : $"{bytes / (1024d * 1024 * 1024):N1} Go";
}