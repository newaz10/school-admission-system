using System.Text.RegularExpressions;
using AdmissionApi.Api;
using AdmissionApi.Data;
using AdmissionApi.Dtos;
using AdmissionApi.Entities;
using Microsoft.EntityFrameworkCore;

namespace AdmissionApi.Endpoints;

public static class HoldEndpoints
{
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
            var studentId = http.Request.Headers["X-Student-Id"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(studentId) || !StudentIdRegex.IsMatch(studentId))
            {
                return Results.Json(
                    new ApiError("X-Student-Id header is required and must be exactly 17 digits."), 
                    statusCode: 400);
            }

            var durationSeconds = config.GetValue<int>("HOLD_DURATION_SECONDS", 600);
            if (durationSeconds <= 0) durationSeconds = 600;

            await using var transaction = await db.Database.BeginTransactionAsync();

            var lockedClass = await db.Classes
                .FromSqlRaw("SELECT \"Id\", \"Name\", \"TotalSeats\", \"CreatedAt\" FROM classes WHERE \"Id\" = {0} FOR UPDATE", classId)
                .FirstOrDefaultAsync();

            if (lockedClass == null)
            {
                return Results.Json(new ApiError("Class not found."), statusCode: 404);
            }

            var now = DateTime.UtcNow;

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

        // 5) GET /holds/{holdId} - check status of a specific application
        app.MapGet("/holds/{holdId}", async (string holdId, AdmissionDbContext db) =>
        {
            var hold = await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId);
            if (hold == null)
            {
                return Results.Json(new ApiError("Hold not found."), statusCode: 404);
            }

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

        // 6) POST /holds/{holdId}/confirm - pay fee and confirm admission
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

            // Idempotency: if already confirmed, just return success
            if (hold.Status == HoldStatus.CONFIRMED)
            {
                await transaction.CommitAsync();
                return Results.Json(new { holdId = hold.Id, status = HoldStatus.CONFIRMED.ToString() });
            }

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

        // 7) DELETE /holds/{holdId} - cancel application
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

            // Idempotency: if already cancelled, just return 204
            if (hold.Status == HoldStatus.CANCELLED)
            {
                await transaction.CommitAsync();
                return Results.NoContent();
            }

            if (hold.Status != HoldStatus.HELD || hold.ExpiresAt <= now)
            {
                return Results.Json(new ApiError("Only active, unexpired holds can be cancelled."), statusCode: 400);
            }

            hold.Status = HoldStatus.CANCELLED;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            return Results.NoContent();
        });
    }
}