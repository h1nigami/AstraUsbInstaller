using Microsoft.Data.Sqlite;

namespace AstraUsb.Services;

/// <summary>Подразделение. Отделы вложенные: у подчинённого задан родитель.</summary>
public sealed record Department(long Id, string Code, string Name, long? ParentId);

/// <summary>Сотрудник, за которым закреплена камера.</summary>
public sealed record Employee(
    long Id,
    string PersonnelNo,
    string FullName,
    string Phone,
    string Role,
    long? DepartmentId,
    bool Active);

/// <summary>
/// Справочник отделов и сотрудников.
///
/// В прежней версии за человека отвечало одно текстовое поле у устройства.
/// Этого не хватало: по инструкции к станции сотрудник заводится карточкой:
/// персональный номер, имя, телефон, роль, отдел. Отделы образуют дерево.
/// Старое поле не выбрасывается: при первом запуске из него создаются
/// карточки, чтобы накопленные записи не потерялись.
/// </summary>
public sealed class StaffDirectory
{
    private readonly string _dbPath;

    public StaffDirectory(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        using var db = Open();

        Run(db, """
            CREATE TABLE IF NOT EXISTS departments (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                code      TEXT DEFAULT '',
                name      TEXT NOT NULL,
                parent_id INTEGER REFERENCES departments(id)
            )
            """);

        Run(db, """
            CREATE TABLE IF NOT EXISTS employees (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                personnel_no  TEXT UNIQUE,
                full_name     TEXT NOT NULL,
                phone         TEXT DEFAULT '',
                role          TEXT DEFAULT '',
                department_id INTEGER REFERENCES departments(id),
                active        INTEGER NOT NULL DEFAULT 1
            )
            """);

        // Привязка камеры к сотруднику. Колонку добавляем к существующей
        // таблице устройств: базы на станциях уже накоплены.
        TryRun(db, "ALTER TABLE devices ADD COLUMN employee_id INTEGER REFERENCES employees(id)");

        MigrateLegacyPersons(db);
    }

    /// <summary>
    /// Переносит прежнее текстовое поле «человек» в карточки сотрудников.
    /// Выполняется один раз: повторный запуск ничего не дублирует.
    /// </summary>
    private static void MigrateLegacyPersons(SqliteConnection db)
    {
        try
        {
            using var read = db.CreateCommand();
            read.CommandText = """
                SELECT DISTINCT person FROM devices
                WHERE person IS NOT NULL AND person <> ''
                """;

            var names = new List<string>();
            using (var reader = read.ExecuteReader())
                while (reader.Read())
                    names.Add(reader.GetString(0));

            foreach (var name in names)
            {
                Run(db, """
                    INSERT INTO employees (personnel_no, full_name)
                    SELECT NULL, $name
                    WHERE NOT EXISTS (SELECT 1 FROM employees WHERE full_name = $name)
                    """, ("$name", name));

                Run(db, """
                    UPDATE devices SET employee_id = (
                        SELECT id FROM employees WHERE full_name = $name LIMIT 1)
                    WHERE person = $name AND employee_id IS NULL
                    """, ("$name", name));
            }
        }
        catch (SqliteException)
        {
            // Старой колонки нет, переносить нечего.
        }
    }

    // --- Отделы -------------------------------------------------------------

    public long AddDepartment(string name, string code = "", long? parentId = null)
    {
        using var db = Open();
        Run(db, """
            INSERT INTO departments (code, name, parent_id)
            VALUES ($code, $name, $parent)
            """, ("$code", code), ("$name", name), ("$parent", (object?)parentId ?? DBNull.Value));
        return (long)(Scalar(db, "SELECT last_insert_rowid()") ?? 0L);
    }

    public void RenameDepartment(long id, string name, string code)
    {
        using var db = Open();
        Run(db, "UPDATE departments SET name = $name, code = $code WHERE id = $id",
            ("$name", name), ("$code", code), ("$id", id));
    }

    /// <summary>
    /// Удаляет отдел. Подчинённые поднимаются к родителю удалённого, а не
    /// пропадают вместе с ним: терять сотрудников из-за перестановки в
    /// структуре недопустимо.
    /// </summary>
    public void DeleteDepartment(long id)
    {
        using var db = Open();
        var parent = Scalar(db, "SELECT parent_id FROM departments WHERE id = $id", ("$id", id));

        Run(db, "UPDATE departments SET parent_id = $parent WHERE parent_id = $id",
            ("$parent", parent ?? DBNull.Value), ("$id", id));
        Run(db, "UPDATE employees SET department_id = $parent WHERE department_id = $id",
            ("$parent", parent ?? DBNull.Value), ("$id", id));
        Run(db, "DELETE FROM departments WHERE id = $id", ("$id", id));
    }

    public IReadOnlyList<Department> Departments()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, code, name, parent_id FROM departments ORDER BY name";

        var list = new List<Department>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Department(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        }
        return list;
    }

    /// <summary>Путь отдела сверху вниз: «Охрана / Смена 1».</summary>
    public string DepartmentPath(long? id)
    {
        if (id is null)
            return "";

        var all = Departments().ToDictionary(d => d.Id);
        var parts = new List<string>();
        var current = id;

        // Ограничение на глубину: битая ссылка на самого себя не должна зациклить.
        for (var depth = 0; current is { } key && all.TryGetValue(key, out var dep) && depth < 32; depth++)
        {
            parts.Insert(0, dep.Name);
            current = dep.ParentId;
        }

        return string.Join(" / ", parts);
    }

    // --- Сотрудники ---------------------------------------------------------

    public long AddEmployee(string fullName, string personnelNo = "", string phone = "",
        string role = "", long? departmentId = null)
    {
        using var db = Open();
        Run(db, """
            INSERT INTO employees (personnel_no, full_name, phone, role, department_id, active)
            VALUES ($no, $name, $phone, $role, $dep, 1)
            """,
            ("$no", string.IsNullOrEmpty(personnelNo) ? DBNull.Value : personnelNo),
            ("$name", fullName), ("$phone", phone), ("$role", role),
            ("$dep", (object?)departmentId ?? DBNull.Value));
        return (long)(Scalar(db, "SELECT last_insert_rowid()") ?? 0L);
    }

    public void UpdateEmployee(Employee employee)
    {
        using var db = Open();
        Run(db, """
            UPDATE employees
            SET personnel_no = $no, full_name = $name, phone = $phone,
                role = $role, department_id = $dep, active = $active
            WHERE id = $id
            """,
            ("$no", string.IsNullOrEmpty(employee.PersonnelNo) ? DBNull.Value : employee.PersonnelNo),
            ("$name", employee.FullName), ("$phone", employee.Phone), ("$role", employee.Role),
            ("$dep", (object?)employee.DepartmentId ?? DBNull.Value),
            ("$active", employee.Active ? 1 : 0), ("$id", employee.Id));
    }

    /// <summary>
    /// Уволенного сотрудника помечаем неактивным, а не удаляем: за ним
    /// числятся прошлые записи, и они должны остаться подписанными.
    /// </summary>
    public void Deactivate(long employeeId)
    {
        using var db = Open();
        Run(db, "UPDATE employees SET active = 0 WHERE id = $id", ("$id", employeeId));
    }

    public IReadOnlyList<Employee> Employees(bool activeOnly = false)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, COALESCE(personnel_no, ''), full_name, phone, role, department_id, active
            FROM employees
            """ + (activeOnly ? " WHERE active = 1" : "") + " ORDER BY full_name";

        var list = new List<Employee>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Employee(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6) != 0));
        }
        return list;
    }

    public Employee? FindByPersonnelNo(string personnelNo) =>
        Employees().FirstOrDefault(e =>
            string.Equals(e.PersonnelNo, personnelNo, StringComparison.OrdinalIgnoreCase));

    // --- Привязка камеры ----------------------------------------------------

    public void AssignDevice(long deviceId, long? employeeId)
    {
        using var db = Open();
        Run(db, "UPDATE devices SET employee_id = $emp WHERE id = $id",
            ("$emp", (object?)employeeId ?? DBNull.Value), ("$id", deviceId));
    }

    public Employee? EmployeeOfDevice(long deviceId)
    {
        using var db = Open();
        var id = Scalar(db, "SELECT employee_id FROM devices WHERE id = $id", ("$id", deviceId));
        return id is long employeeId ? Employees().FirstOrDefault(e => e.Id == employeeId) : null;
    }

    // --- Служебное ----------------------------------------------------------

    private SqliteConnection Open()
    {
        var db = new SqliteConnection($"Data Source={_dbPath}");
        db.Open();
        return db;
    }

    private static void Run(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static void TryRun(SqliteConnection db, string sql)
    {
        try
        {
            Run(db, sql);
        }
        catch (SqliteException)
        {
            // Колонка уже существует, обычное дело при миграции.
        }
    }

    private static object? Scalar(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        var value2 = cmd.ExecuteScalar();
        return value2 is DBNull ? null : value2;
    }
}
