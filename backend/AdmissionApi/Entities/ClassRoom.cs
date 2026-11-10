namespace AdmissionApi.Entities;

// One class/shift combination, e.g. "Class 6 - Morning Shift", with its own seat pool.
public class ClassRoom
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int TotalSeats { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<Hold> Holds { get; set; } = new();
}