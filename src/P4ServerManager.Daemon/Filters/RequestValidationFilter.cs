using System.Text.Json;

using FluentValidation;
using FluentValidation.Results;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace P4ServerManager.Daemon.Filters;

/// <summary>
/// Runs the <see cref="IValidator{T}"/> registered for each action argument and adds its errors to the model state,
/// keyed by the JSON names like the errors of MVC. <c>[ApiController]</c> then answers them with a 400.
/// </summary>
public sealed class RequestValidationFilter(IOptions<JsonOptions> jsonOptions) : IAsyncActionFilter
{
    /// <summary>
    /// Runs before the filter of <c>[ApiController]</c> that rejects an invalid model state, whose order is -2000.
    /// </summary>
    public const int Order = -2100;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // The validators can rely on what MVC already checks, such as the [Required] members: they only run on a
        // request that is otherwise valid.
        if (context.ModelState.IsValid)
        {
            foreach (ParameterDescriptor parameter in context.ActionDescriptor.Parameters)
            {
                if (!context.ActionArguments.TryGetValue(parameter.Name, out object? argument) || argument is null)
                {
                    continue;
                }

                Type validatorType = typeof(IValidator<>).MakeGenericType(parameter.ParameterType);
                if (context.HttpContext.RequestServices.GetService(validatorType) is not IValidator validator)
                {
                    continue;
                }

                ValidationResult result = await validator.ValidateAsync(
                    new ValidationContext<object>(argument),
                    context.HttpContext.RequestAborted);
                foreach (ValidationFailure failure in result.Errors)
                {
                    context.ModelState.AddModelError(ToJsonPath(failure.PropertyName), failure.ErrorMessage);
                }
            }
        }

        await next();
    }

    // FluentValidation names the members as in C#, for example SuperUser.Password.
    private string ToJsonPath(string propertyPath)
    {
        JsonNamingPolicy? namingPolicy = jsonOptions.Value.JsonSerializerOptions.PropertyNamingPolicy;
        return namingPolicy is null
            ? propertyPath
            : string.Join('.', propertyPath.Split('.').Select(namingPolicy.ConvertName));
    }
}
