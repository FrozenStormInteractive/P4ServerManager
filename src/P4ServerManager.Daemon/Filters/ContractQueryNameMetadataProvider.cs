using System.Text.Json;

using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

using P4ServerManager.Contracts;

namespace P4ServerManager.Daemon.Filters;

/// <summary>
/// Binds the properties of a request type of <c>P4ServerManager.Contracts</c> read from the query string under their
/// JSON names, such as <c>serverDirectory</c>. The model state keys the values it binds by that name, and its keys
/// ignore the case: an error of <see cref="RequestValidationFilter"/> would otherwise land under the C# name. The values
/// of the query string are still matched whatever their case.
/// </summary>
public sealed class ContractQueryNameMetadataProvider : IBindingMetadataProvider
{
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (context.Key.MetadataKind == ModelMetadataKind.Property
            && context.Key.ContainerType?.Assembly == typeof(CreateInstanceRequest).Assembly
            && context.Key.Name is { } name
            && context.BindingMetadata.BinderModelName is null)
        {
            context.BindingMetadata.BinderModelName = JsonNamingPolicy.CamelCase.ConvertName(name);
        }
    }
}
