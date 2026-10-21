using System.Globalization;
using allstarr.Core.Music;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    [HttpGet, HttpPost]
    [Route("rest/setRating")]
    [Route("rest/setRating.view")]
    public async Task<IActionResult> SetRating()
    {
        var parameters = await ExtractAllParameters();
        var id = parameters.GetValueOrDefault("id", "");
        if (!id.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
            return await RelayMutation("rest/setRating", parameters);
        var format = parameters.GetValueOrDefault("f", "xml");
        if (!int.TryParse(parameters.GetValueOrDefault("rating"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating) ||
            rating is < 0 or > 5 || parameters.GetAllValues("id").Count != 1)
            return _responseBuilder.CreateError(format, 10, "A single item and a rating from 0 to 5 are required");
        var state = HttpContext.RequestServices.GetService<ProviderUserDataStore>();
        if (state == null || !await state.SetRatingAsync(CurrentProtocolContext, id, rating, HttpContext.RequestAborted))
            return _responseBuilder.CreateError(format, 50, "This item is not available to this user");
        return _responseBuilder.CreateOkResponse(format);
    }
}
