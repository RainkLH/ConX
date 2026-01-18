namespace ConX.Models;

public record LoginDto(string UserName, string Password);
public record KillDto(string? Reason);
public record RegisterDto(string UserName, string Password);
