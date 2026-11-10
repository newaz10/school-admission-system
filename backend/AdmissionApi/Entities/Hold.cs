namespace AdmissionApi.Entities;

// One admission application: a seat held (or confirmed) for one student in one class.
public class Hold
{
    public string Id { get; set; } = string.Empty;
    public string ClassId { get; set; } = string.Empty;
    public string StudentId { get; set; } = string.Empty;
    public HoldStatus Status { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public ClassRoom Class { get; set; } = null!;
}