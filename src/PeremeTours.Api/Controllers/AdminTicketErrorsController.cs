using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PeremeTours.Application.Tickets;
using PeremeTours.Domain.Tickets;
using PeremeTours.Domain.Users;

namespace PeremeTours.Api.Controllers;

[ApiController]
[Authorize(Roles = UserRoles.Admin)]
[Route("api/v1/admin/ticket-errors")]
public sealed class AdminTicketErrorsController(ITicketErrorService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<TicketErrorPage>> List([FromQuery, StringLength(160)] string? search,
        [FromQuery, EnumDataType(typeof(TicketErrorStage))] TicketErrorStage? stage,
        [FromQuery, Range(1, 100000)] int page = 1, [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken cancellationToken = default)
        => Ok(await service.ListAsync(search, stage, page, pageSize, cancellationToken));
}
