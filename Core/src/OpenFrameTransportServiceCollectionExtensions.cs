namespace BlueHeighliner.OpenFrameTransport;

/// <summary>
/// Extension methods for registering Open Frame Transport services into an
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class OpenFrameTransportServiceCollectionExtensions
{
    /// <summary>
    /// Registers Open Frame Transport's entry-point services (<see cref="IOftConnector"/>,
    /// <see cref="IOftHoster"/>, and <see cref="IOftPeerFactory"/>) by convention: no explicit
    /// registration is required, since every public interface named <c>IThing</c> in this assembly
    /// is resolved to the public class named <c>Thing</c> that implements it.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddOpenFrameTransport(this IServiceCollection services)
    {
        return services.AddServicesByConvention(typeof(OpenFrameTransportServiceCollectionExtensions).Assembly);
    }

    /// <summary>
    /// Scans an assembly and registers every public interface named <c>IThing</c> to resolve to the
    /// public, non-abstract, same-namespace class named <c>Thing</c> that implements it, without
    /// requiring explicit registration of each pair. Interfaces with no matching implementation in
    /// the assembly are left unregistered.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="assembly">The assembly to scan for interface/implementation pairs.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddServicesByConvention(this IServiceCollection services, Assembly assembly)
    {
        Type[] types = assembly.GetTypes();

        foreach (Type @interface in types.Where(type => type is { IsInterface: true, IsPublic: true }))
        {
            string implementationName = @interface.Name.StartsWith('I') ? @interface.Name[1..] : @interface.Name;

            Type? implementation = types.FirstOrDefault(type =>
                type is { IsClass: true, IsAbstract: false, IsPublic: true } &&
                type.Namespace == @interface.Namespace &&
                type.Name == implementationName &&
                @interface.IsAssignableFrom(type));

            if (implementation is not null)
            {
                services.AddTransient(@interface, implementation);
            }
        }

        return services;
    }
}
