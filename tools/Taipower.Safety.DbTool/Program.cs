using Dapper;
using Microsoft.Data.SqlClient;
using Taipower.Safety.Core.Org;
using Taipower.Safety.Core.Security;
using Taipower.Safety.Infrastructure.Data;
using Taipower.Safety.Infrastructure.Migration;

// Usage:
//   DbTool migrate        --db "<conn>"
//   DbTool create-admin   --db "<conn>" --account admin --employee A00001 --name 系統管理員 --password <pwd>
//   DbTool import-legacy  --db "<conn>" --legacy "<conn to old TaipowerReport>" [--legacy-files <old FileUpload root>] [--files <new storage root>]

var cmd = args.FirstOrDefault();
string? Arg(string name)
{
    var i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
string Required(string name) => Arg(name) ?? throw new ArgumentException($"--{name} is required");

try
{
    switch (cmd)
    {
        case "migrate":
        {
            var (ok, error, applied) = DatabaseMigrator.Upgrade(Required("db"), ensureDatabase: true);
            Console.WriteLine(ok ? $"OK. Applied {applied.Count} script(s)." : $"FAILED: {error}");
            return ok ? 0 : 1;
        }
        case "create-admin":
        {
            await using var conn = new SqlConnection(Required("db"));
            var employeeId = Required("employee");
            await conn.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM dbo.Employee WHERE EmployeeId = @employeeId)
                    INSERT dbo.Employee (EmployeeId, Name, RoleId, IsActive) VALUES (@employeeId, @name, @role, 1);
                INSERT dbo.UserAccount (Account, EmployeeId, PasswordHash, PasswordChangedAt)
                VALUES (@account, @employeeId, @hash, SYSDATETIME());
                """, new
            {
                employeeId,
                name = Arg("name") ?? "系統管理員",
                role = Roles.SystemAdmin,
                account = Required("account"),
                hash = PasswordHasher.Hash(Required("password")),
            });
            Console.WriteLine("Admin account created.");
            return 0;
        }
        case "import-legacy":
        {
            var importer = new LegacyImporter(Required("legacy"), Required("db"), Arg("legacy-files"), Arg("files"), Console.Out);
            await importer.RunAsync();
            return 0;
        }
        default:
            Console.WriteLine("Commands: migrate | create-admin | import-legacy  (see Program.cs header for options)");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
