using System.Text.RegularExpressions;
using AdmissionApi.Api;
using AdmissionApi.Data;
using AdmissionApi.Dtos;
using AdmissionApi.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdmissionApi.Endpoints;

// Endpoints for seat holds: applying, checking, confirming (paying),
// cancelling and listing the current student's applications.
public static class HoldEndpoints
{
    // The assignment requires the student identity to be a 17-digit birth registration number.
    private static readonly Regex StudentIdRegex = new(@"^\d{17}$", RegexOptions.Compiled);

    public static void MapHoldEndpoints(this WebApplication app)
    {
        // 4) POST /classes/{classId}/holds - apply for a seat (create a hold).
        app.MapPost("/classes/{classId}/holds", async (
            string classId,
            HttpContext http,
            AdmissionDbContext db,
            IConfiguration config) =>
        {
            var studentId = http.Request.Headers["X-Student-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(studentId) || !StudentIdRegex.IsMatch(studentId))
            {
                return Results.Json(
                    new ApiError("X-Student-Id header is required and must be exactly 17 digits."),
                    statusCode: 400);
            }

            // Hold duration is configurable via environment variable (default 600 seconds).
            var durationSeconds = config.GetValue<int>("HOLD_DURATION_SECONDS", 600);
            if (durationSeconds <= 0) durationSeconds = 600;

            await using var transaction = await db.Database.BeginTransactionAsync();

            // Lock the class row so concurrent applications are processed one at a time.
            // This is what guarantees held + confirmed never exceeds totalSeats.
            var lockedClass = await db.Classes
                .FromSqlRaw("SELECT \"Id\", \"Name\", \"TotalSeats\", \"CreatedAt\" FROM classes WHERE \"Id\" = {0} FOR UPDATE", classId)
                .FirstOrDefaultAsync();

            if (lockedClass == null)
            {
                return Results.Json(new ApiError("Class not found."), statusCode: 404);
            }

            var now = DateTime.UtcNow;

            // One active application (unexpired HELD or CONFIRMED) per student per class.
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

            // Seats currently blocked by unexpired holds or confirmed admissions.
            var heldCount = await db.Holds.CountAsync(h =>
                h.ClassId == classId && h.Status == HoldStatus.HELD && h.ExpiresAt > now);

            var confirmedCount = await db.Holds.CountAsync(h =>
                h.ClassId == classId && h.Status == HoldStatus.CONFIRMED);

            if (heldCount + confirmedCount >= lockedClass.TotalSeats)
            {
                return Results.Json(new ApiError("No seats available."), statusCode: 409);
            }

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

        // 5) GET /holds/{holdId} - current status of one application.
        app.MapGet("/holds/{holdId}", async (string holdId, AdmissionDbContext db) =>
        {
            var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId);
            if (hold == null)
            {
                return Results.Json(new ApiError("Hold not found."), statusCode: 404);
            }

            var effectiveStatus = EffectiveStatus(hold.Status, hold.ExpiresAt, DateTime.UtcNow);

            return Results.Json(new HoldDetailsResponse(
                hold.Id,
                hold.ClassId,
                effectiveStatus.ToString(),
                hold.ExpiresAt));
        });

        // 6) POST /holds/{holdId}/confirm - pay the fee and confirm the admission.
        app.MapPost("/holds/{holdId}/confirm", async (
            string holdId,
            HttpContext http,
            AdmissionDbContext db) =>
        {
            var studentId = http.Request.Headers["X-Student-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(studentId) || !StudentIdRegex.IsMatch(studentId))
            {
                return Results.Json(
                    new ApiError("X-Student-Id header is required and must be exactly 17 digits."),
                    statusCode: 400);
            }

            await using var transaction = await db.Database.BeginTransactionAsync();

            // Lock the hold row so a simultaneous confirm + cancel cannot interleave.
            var hold = await db.Holds
                .FromSqlRaw("SELECT \"Id\", \"ClassId\", \"StudentId\", \"Status\", \"ExpiresAt\", \"CreatedAt\", \"ConfirmedAt\" FROM holds WHERE \"Id\" = {0} FOR UPDATE", holdId)
                .FirstOrDefaultAsync();

            if (hold == null)
            {
                return Results.Json(new ApiError("Hold not found."), statusCode: 404);
            }

            if (hold.StudentId != studentId)
            {
                return Results.Json(new ApiError("You can only confirm your own holds."), statusCode: 403);
            }

            var now = DateTime.UtcNow;

            // Idempotency: repeating a confirm on an already confirmed hold changes nothing.
            if (hold.Status == HoldStatus.CONFIRMED)
            {
                await transaction.CommitAsync();
                return Results.Json(new { holdId = hold.Id, status = HoldStatus.CONFIRMED.ToString() });
            }

            // Only an unexpired HELD hold can be confirmed.
            if (hold.Status != HoldStatus.HELD || hold.ExpiresAt <= now)
            {
                return Results.Json(new ApiError("This hold has expired or is no longer active."), statusCode: 400);
            }

            hold.Status = HoldStatus.CONFIRMED;
            hold.ConfirmedAt = now;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return Results.Json(new { holdId = hold.Id, status = HoldStatus.CONFIRMED.ToString() });
        });

        // 7) DELETE /holds/{holdId} - cancel an application.
        app.MapDelete("/holds/{holdId}", async (
            string holdId,
            HttpContext http,
            AdmissionDbContext db) =>
        {
            var studentId = http.Request.Headers["X-Student-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(studentId) || !StudentIdRegex.IsMatch(studentId))
            {
                return Results.Json(
                    new ApiError("X-Student-Id header is required and must be exactly 17 digits."),
                    statusCode: 400);
            }

            await using var transaction = await db.Database.BeginTransactionAsync();

            var hold = await db.Holds
                .FromSqlRaw("SELECT \"Id\", \"ClassId\", \"StudentId\", \"Status\", \"ExpiresAt\", \"CreatedAt\", \"ConfirmedAt\" FROM holds WHERE \"Id\" = {0} FOR UPDATE", holdId)
                .FirstOrDefaultAsync();

            if (hold == null)
            {
                return Results.Json(new ApiError("Hold not found."), statusCode: 404);
            }

            if (hold.StudentId != studentId)
            {
                return Results.Json(new ApiError("You can only cancel your own holds."), statusCode: 403);
            }

            var now = DateTime.UtcNow;

            // Idempotency: cancelling an already cancelled hold is still a success.
            if (hold.Status == HoldStatus.CANCELLED)
            {
                await transaction.CommitAsync();
                return Results.NoContent();
            }

            // Only an unexpired HELD hold can be cancelled.
            if (hold.Status != HoldStatus.HELD || hold.ExpiresAt <= now)
            {
                return Results.Json(new ApiError("Only active, unexpired holds can be cancelled."), statusCode: 400);
            }

            hold.Status = HoldStatus.CANCELLED;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return Results.NoContent();
        });

        // 8) GET /me/holds - every application of the current student, newest first.
        app.MapGet("/me/holds", async (HttpContext http, AdmissionDbContext db) =>
        {
            var studentId = http.Request.Headers["X-Student-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(studentId) || !StudentIdRegex.IsMatch(studentId))
            {
                return Results.Json(
                    new ApiError("X-Student-Id header is required and must be exactly 17 digits."),
                    statusCode: 400);
            }

            var now = DateTime.UtcNow;

            var rows = await db.Holds
                .Where(h => h.StudentId == studentId)
                .OrderByDescending(h => h.CreatedAt)
                .Select(h => new
                {
                    h.Id,
                    h.ClassId,
                    ClassName = h.Class.Name,
                    h.Status,
                    h.ExpiresAt
                })
                .ToListAsync();

            var response = rows
                .Select(r => new MyHoldResponse(
                    r.Id,
                    r.ClassId,
                    r.ClassName,
                    EffectiveStatus(r.Status, r.ExpiresAt, now).ToString(),
                    r.ExpiresAt))
                .ToList();

            return Results.Json(response);
        });
    }

    // A HELD hold whose expiry time has passed is reported as EXPIRED immediately,
    // even before the background sweeper (added in the next step) updates the stored row.
    private static HoldStatus EffectiveStatus(HoldStatus status, DateTime expiresAt, DateTime now) =>
        status == HoldStatus.HELD && expiresAt <= now ? HoldStatus.EXPIRED : status;
}