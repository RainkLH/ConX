namespace ConX.Models;

public class User
{
    public int Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool IsAdmin { get; set; } = true;
    public DateTime CreateTime { get; set; } = DateTime.Now;
}
