namespace AdmissionApi.Api;

// Uniform JSON body for every 4xx error response in the API.
public sealed record ApiError(string Error);