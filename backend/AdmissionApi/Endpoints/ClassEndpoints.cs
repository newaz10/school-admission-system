using AdmissionApi.Api;
using AdmissionApi.Data;
using AdmissionApi.Dtos;
using AdmissionApi.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdmissionApi.Endpoints;

// Endpoints for managing classes and reading their seat situation.
public static class ClassEndpoints
{
    private const int MaxNameLength = 100;
    private const int MaxTotalSeats = 100000;

    public static void MapClassEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/classes");

        // 1) POST /classes - create a new class.
        group.MapPost("", async (CreateClassRequest? body, AdmissionDbContext db) =>
        {
            var name = body?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.Json(new ApiError("Class name is required."), statusCode: 400);
            }

            if (name.Length > MaxNameLength)
            {
                return Results.Json(
                    new ApiError($"Class name must be at most {MaxNameLength} characters."),
                    statusCode: 400);
            }

            var totalSeats = body?.TotalSeats;
            if (totalSeats is null || totalSeats < 1 || totalSeats > MaxTotalSeats)
            {
                return Results.Json(
                    new ApiError($"totalSeats must be a whole number between 1 and {MaxTotalSeats}."),
                    statusCode: 400);
            }

            var classRoom = new ClassRoom
            {
                Id = "c_" + Guid.NewGuid().ToString("N"),
                Name = name,
                TotalSeats = totalSeats.Value,
                CreatedAt = DateTime.UtcNow
            };

            db.Classes.Add(classRoom);
            await db.SaveChangesAsync();

            return Results.Json(
                new ClassCreatedResponse(classRoom.Id, classRoom.Name, classRoom.TotalSeats),
                statusCode: 201);
        });

        // 2) GET /classes - all classes with their seat counts.
        group.MapGet("", async (AdmissionDbContext db) =>
        {
            var summaries = await LoadSummaries(db, classId: null);
            return Results.Json(summaries);
        });

        // 3) GET /classes/{classId} - seat situation of one class.
        group.MapGet("/{classId}", async (string classId, AdmissionDbContext db) =>
        {
            var summaries = await LoadSummaries(db, classId);
            if (summaries.Count == 0)
            {
                return Results.Json(new ApiError("Class not found."), statusCode: 404);
            }

            return Results.Json(summaries[0]);
        });
    }

    // Seat counts are always derived from the holds table:
    private static async Task<List<ClassSummaryResponse>> LoadSummaries(
        AdmissionDbContext db, string? classId)
    {
        var now = DateTime.UtcNow;

        var rows = await db.Classes
            .Where(c => classId == null || c.Id == classId)
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.TotalSeats,
                HeldSeats = c.Holds.Count(h => h.Status == HoldStatus.HELD && h.ExpiresAt > now),
                ConfirmedSeats = c.Holds.Count(h => h.Status == HoldStatus.CONFIRMED)
            })
            .ToListAsync();

        return rows
            .Select(r => new ClassSummaryResponse(
                r.Id,
                r.Name,
                r.TotalSeats,
                r.HeldSeats,
                r.ConfirmedSeats,
                Math.Max(0, r.TotalSeats - r.HeldSeats - r.ConfirmedSeats)))
            .ToList();
    }
}