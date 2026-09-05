using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

public static class UserError
{
    public static string Report(string action, Exception error)
    {
        CrashLog.Write(action, error);
        var reason = error switch
        {
            FileNotFoundException or DirectoryNotFoundException =>
                "Файл или папка недоступны. Проверьте подключение носителя и обновите список.",
            UnauthorizedAccessException =>
                "Нет доступа к файлу или папке. Выберите доступную папку или обратитесь к администратору.",
            SqliteException { SqliteErrorCode: 5 or 6 } =>
                "База занята другой операцией. Подождите и повторите действие.",
            SqliteException { SqliteErrorCode: 11 or 26 } =>
                "Не удалось прочитать базу станции. Обратитесь к администратору для её проверки.",
            SqliteException =>
                "База станции недоступна для этого действия. Повторите попытку; если ошибка останется, обратитесь к администратору.",
            OperationCanceledException => "Операция прервана. При необходимости запустите её снова.",
            TimeoutException or System.Net.Sockets.SocketException or System.Net.WebException =>
                "Нет ответа от сервера. Проверьте сеть и параметры подключения.",
            ArgumentException or FormatException or NotSupportedException =>
                "Проверьте введённые значения, путь и формат файла.",
            IOException =>
                "Проверьте подключение носителя, свободное место и доступ к файлу. Затем повторите действие.",
            _ => "Повторите действие. Если ошибка повторится, обратитесь к администратору.",
        };
        return $"{action.Trim().TrimEnd('.', ':')}. {reason}";
    }
}
