namespace AdmissionApi.Entities;

// Lifecycle of a single admission application.
public enum HoldStatus
{
    HELD,
    CONFIRMED,
    EXPIRED,
    CANCELLED
}