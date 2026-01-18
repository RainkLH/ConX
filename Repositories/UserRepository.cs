using SqlSugar;
using ConX.Models;
using Microsoft.AspNetCore.Identity;

namespace ConX.Repositories;

public class UserRepository
{
    private readonly SqlSugarClient _db;
    public UserRepository()
    {
        var connStr = "Data Source=sysadmin.db";
        _db = new SqlSugarClient(new ConnectionConfig { ConnectionString = connStr, DbType = DbType.Sqlite, InitKeyType = InitKeyType.Attribute, IsAutoCloseConnection = true });
        _db.CodeFirst.InitTables<User>();
        if (!_db.Queryable<User>().Any())
        {
            var hasher = new PasswordHasher<User>();
            var u = new User { UserName = "admin" };
            u.PasswordHash = hasher.HashPassword(u, "admin");
            _db.Insertable(u).ExecuteCommand();
        }
    }

    public User? GetUserByName(string userName)
    {
        return _db.Queryable<User>().Where(u => u.UserName == userName).First();
    }

    public bool UserExists(string userName)
    {
        return _db.Queryable<User>().Any(u => u.UserName == userName);
    }

    public bool CreateUser(string userName, string password)
    {
        if (UserExists(userName)) return false;
        var user = new User { UserName = userName };
        var hasher = new PasswordHasher<User>();
        user.PasswordHash = hasher.HashPassword(user, password);
        _db.Insertable(user).ExecuteCommand();
        return true;
    }
}
