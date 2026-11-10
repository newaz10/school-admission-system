namespace AdmissionApi.Dtos;

// 201 response of POST /classes/{classId}/holds
public sealed record HoldCreatedResponse(
    string HoldId,
    string ClassId,
    string StudentId,
    string Status,
    DateTime ExpiresAt);

// 200 response of GET /holds/{holdId}
public sealed record HoldDetailsResponse(
    string HoldId,
    string ClassId,
    string Status,
    DateTime ExpiresAt);

// 200 response of GET /me/holds
public sealed record MyHoldResponse(
    string HoldId,
    string ClassId,
    string ClassName,
    string Status,
    DateTime ExpiresAt);