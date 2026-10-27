using Microsoft.AspNetCore.Mvc;
using Zayra.Api.Data;

namespace Zayra.Api.Controllers;

/// <summary>Skeleton (TDD red): routes arrive with the implementation commit.</summary>
[ApiController]
public sealed class HrProfileCompletionController(ZayraDbContext db) : ControllerBase { private readonly ZayraDbContext _db = db; }
