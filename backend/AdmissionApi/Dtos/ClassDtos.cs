namespace AdmissionApi.Dtos;

// Request body of POST /classes.
// Nullable properties let us produce friendly 400 messages ourselves
// instead of relying on framework defaults.
public sealed record CreateClassRequest(string? Name, int? TotalSeats);

// 201 response of POST /classes.
public sealed record ClassCreatedResponse(string Id, string Name, int TotalSeats);

// 200 response of GET /classes and GET /classes/{classId}.
public sealed record ClassSummaryResponse(
    string Id,
    string Name,
    int TotalSeats,
    int HeldSeats,
    int ConfirmedSeats,
    int AvailableSeats);