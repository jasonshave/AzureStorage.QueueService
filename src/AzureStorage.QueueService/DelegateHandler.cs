using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorage.QueueService;

/// <summary>
/// How a lambda handler's parameters are filled, worked out once at registration: the message body, the message, the
/// cancellation token, or a service from the message's scope.
/// </summary>
internal sealed class DelegateHandlerBinding<TMessage>
{
    private readonly Delegate _handler;
    private readonly Func<QueueMessage<TMessage>, IServiceProvider, CancellationToken, object?>[] _arguments;

    public DelegateHandlerBinding(Delegate handler)
    {
        _handler = handler;
        var method = handler.Method;

        if (method.ReturnType != typeof(void) && !typeof(Task).IsAssignableFrom(method.ReturnType) && method.ReturnType != typeof(ValueTask))
            throw new ArgumentException($"A handler delegate must return void, Task or ValueTask; this one returns {method.ReturnType.Name}.", nameof(handler));

        _arguments = method.GetParameters().Select(Bind).ToArray();
        Name = method.Name.Contains('<') ? $"lambda handler for {typeof(TMessage).Name}" : method.Name;
    }

    /// <summary>What logs call the handler: the method's name, or a description for a lambda.</summary>
    public string Name { get; }

    private static Func<QueueMessage<TMessage>, IServiceProvider, CancellationToken, object?> Bind(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(TMessage)) return static (message, _, _) => message.Body;
        if (type == typeof(QueueMessage<TMessage>) || type == typeof(IQueueMessage)) return static (message, _, _) => message;
        if (type == typeof(CancellationToken)) return static (_, _, token) => token;
        if (type == typeof(IServiceProvider)) return static (_, services, _) => services;

        if (parameter.GetCustomAttribute<FromKeyedServicesAttribute>() is { } keyed)
        {
            return parameter.HasDefaultValue
                ? (_, services, _) => services.GetKeyedService(type, keyed.Key) ?? parameter.DefaultValue
                : (_, services, _) => services.GetRequiredKeyedService(type, keyed.Key);
        }

        return parameter.HasDefaultValue
            ? (_, services, _) => services.GetService(type) ?? parameter.DefaultValue
            : (_, services, _) => services.GetRequiredService(type);
    }

    public async ValueTask InvokeAsync(QueueMessage<TMessage> message, IServiceProvider services, CancellationToken cancellationToken)
    {
        var arguments = new object?[_arguments.Length];
        for (var i = 0; i < arguments.Length; i++) arguments[i] = _arguments[i](message, services, cancellationToken);

        // DoNotWrapExceptions: the handler's own exception reaches the processor, not a TargetInvocationException.
        var result = _handler.Method.Invoke(_handler.Target, BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null);
        switch (result)
        {
            case Task task:
                await task;
                break;
            case ValueTask valueTask:
                await valueTask;
                break;
        }
    }
}

/// <summary>The handler behind <c>AddProcessor&lt;TMessage&gt;(Delegate)</c>, created per message in its scope.</summary>
internal sealed class DelegateHandler<TMessage>(DelegateHandlerBinding<TMessage> binding, IServiceProvider services) : IQueueMessageHandler<TMessage>
{
    public ValueTask HandleAsync(QueueMessage<TMessage> message, CancellationToken cancellationToken) =>
        binding.InvokeAsync(message, services, cancellationToken);
}
