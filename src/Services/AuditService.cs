namespace ConX.Services;

public class AuditService
{
    private readonly List<string> _logs = new();
    public void Log(string entry)
    {
        lock (_logs)
        {
            _logs.Add($"[{DateTime.Now:O}] {entry}");
            if (_logs.Count > 10000) _logs.RemoveAt(0);
        }
    }

    public IEnumerable<string> Query(int skip = 0, int take = 100)
    {
        lock (_logs)
        {
            return _logs.Skip(skip).Take(take).ToList();
        }
    }
}
