using System.Security.Cryptography;
using System.Text;

namespace AstraUsb.Services;

/// <summary>
/// Пароль станции: им закрыт выход из программы и разделы, кроме «Загрузки».
///
/// В настройках лежит не сам пароль, а его хеш с солью: файл настроек читается
/// любым, кто дотянется до диска, и открытый пароль там означал бы, что защиты
/// нет. Пока пароль не задан, подходит значение по умолчанию, как у
/// Python-версии, иначе первый запуск станции оказался бы запертым.
/// </summary>
public static class PasswordGate
{
    /// <summary>Пароль по первому запуску, тот же, что у Python-версии.</summary>
    public const string Fallback = "exit";

    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    /// <summary>Пароль первого запуска: из окружения, иначе стандартный.</summary>
    public static string Default()
    {
        var fromEnv = Environment.GetEnvironmentVariable("APP_EXIT_PASSWORD");
        return string.IsNullOrEmpty(fromEnv) ? Fallback : fromEnv;
    }

    /// <summary>Хеш для хранения в настройках: «итерации:соль:ключ».</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Derive(password, salt, Iterations);
        return $"{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(key)}";
    }

    /// <summary>
    /// Подходит ли пароль. Если хеш не задан, станция сверяет с паролем
    /// первого запуска.
    /// </summary>
    public static bool Matches(string? stored, string password)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return password == Default();

        var parts = stored.Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var iterations)
            || iterations <= 0)
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Derive(password, salt, iterations, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            // Настройки правили руками и испортили запись. Отказ вернее, чем
            // пустить кого угодно.
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int size = KeySize) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, size);
}
