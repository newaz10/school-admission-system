using System.Text.RegularExpressions;
using AdmissionApi.Api;
using AdmissionApi.Data;
using AdmissionApi.Dtos;
using AdmissionApi.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdmissionApi.Endpoints;

// Endpoints for creating seat holds and checking their status.
public static class HoldEndpoints
{
    // Spec requires exactly 17 digits for the student ID
    private static readonly Regex StudentIdRegex = new(@"^\d{17}$", RegexOptions.Compiled);

    public static void MapHoldEndpoints(this WebApplication app)
    {
        // 4) POST /classes/{classId}/holds - apply for a seat
        app.MapPost("/classes/{classId}/holds", async (
            string classId,
            HttpContext http,
            AdmissionDbContext db,
            IConfiguration config) =>
        {
            // 1. Validate student ID header
            var studentId = http.Request.Headers["X-Student-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(studentId) || !StudentIdRegex.IsMatch(studentId))
            {
                return Results.Json(
                    new ApiError("X-Student-Id header is required and must be exactly 17 digits."), 
                    statusCode: 400);
            }

            // 2. Read hold duration from environment variable (default 600 seconds)
            var durationSeconds = config.GetValue<int>("HOLD_DURATION_SECONDS", 600);
            if (durationSeconds <= 0) durationSeconds = 600;

            // 3. Start a database transaction for safe concurrency
            await using var transaction = await db.Database.BeginTransactionAsync();

            // 4. LOCK the class row. This forces concurrent requests to wait in line,
            // preventing the "overselling seats" race condition.
            var lockedClass = await db.Classes
                .FromSqlRaw("SELECT \"Id\", \"Name\", \"TotalSeats\", \"CreatedAt\" FROM classes WHERE \"Id\" = {0} FOR UPDATE", classId)
                .FirstOrDefaultAsync();

            if (lockedClass == null)
            {
                return Results.Json(new ApiError("Class not found."), statusCode: 404);
            }

            var now = DateTime.UtcNow;

            // 5. Check if this student already has an active application for this class
            var hasActiveApplication = await db.Holds.AnyAsync(h =>
                h.ClassId == classId &&
                h.StudentId == studentId &&
                (h.Status == HoldStatus.CONFIRMED || 
                (h.Status == HoldStatus.HELD && h.ExpiresAt > now)));

            if (hasActiveApplication)
            {
                return Results.Json(
                    new ApiError("You already have an active application or confirmed seat for this class."), 
                    statusCode: 409);
            }

            // 6. Count currently occupied seats (unexpired holds + confirmed)
            var heldCount = await db.Holds.CountAsync(h =>
                h.ClassId == classId && h.Status == HoldStatus.HELD && h.ExpiresAt > now);
            
            var confirmedCount = await db.Holds.CountAsync(h =>
                h.ClassId == classId && h.Status == HoldStatus.CONFIRMED);

            // 7. Check capacity
            if (heldCount + confirmedCount >= lockedClass.TotalSeats)
            {
                return Results.Json(new ApiError("No seats available."), statusCode: 409);
            }

            // 8. Create the hold
            var hold = new Hold
            {
                Id = "h_" + Guid.NewGuid().ToString("N"),
                ClassId = classId,
                StudentId = studentId,
                Status = HoldStatus.HELD,
                ExpiresAt = now.AddSeconds(durationSeconds),
                CreatedAt = now
            };

            db.Holds.Add(hold);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return Results.Json(new HoldCreatedResponse(
                hold.Id,
                hold.ClassId,
                hold.StudentId,
                hold.Status.ToString(),
                hold.ExpiresAt), statusCode: 201);
        });

        // 5) GET /holds/{holdId} - check status of a specific application
        app.MapGet("/holds/{holdId}", async (string holdId, AdmissionDbContext db) =>
        {
            var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId);
            if (hold == null)
            {
                return Results.Json(new ApiError("Hold not found."), statusCode: 404);
            }

            // Calculate "effective" status (HELD becomes EXPIRED if time has passed)
            var effectiveStatus = hold.Status;
            if (hold.Status == HoldStatus.HELD && hold.ExpiresAt <= DateTime.UtcNow)
            {
                effectiveStatus = HoldStatus.EXPIRED;
            }

            return Results.Json(new HoldDetailsResponse(
                hold.Id,
                hold.ClassId,
                effectiveStatus.ToString(),
                hold.ExpiresAt));
        });
    }
}