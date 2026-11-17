using Billing.Api.Infrastructure;
using Billing.Api.Integrations;
using Billing.Api.Messaging;
using Billing.Api.Saga;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<IntegrationExceptionHandler>();
builder.Services.Configure<MercadoPagoOptions>(builder.Configuration.GetSection("MercadoPago"));
builder.Services.AddOptions<PaymentOptions>().BindConfiguration("Payment")
    .Validate(options => options.Provider is PaymentOptions.MercadoPago or PaymentOptions.Fake,
        "Payment:Provider deve ser MercadoPago ou Fake.")
    .Validate<IHostEnvironment>((options, environment) =>
        options.Provider != PaymentOptions.Fake || PaymentOptions.AllowsFake(environment),
        "Payment:Provider=Fake só está disponível em Development ou Testing.")
    .ValidateOnStart();
builder.Services.AddScoped<PaymentProviders>();
builder.Services.AddSingleton<FakePaymentProvider>();
builder.Services.AddOptions<RabbitMqOptions>()
    .BindConfiguration("RabbitMq")
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IPaymentEventPublisher, RabbitMqPaymentEventPublisher>();
builder.Services.AddOptions<BillingSagaOptions>().BindConfiguration("Saga").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton<BillingSagaStore>();
builder.Services.AddSingleton<BillingSagaService>();
builder.Services.AddHostedService<BillingSagaRabbitMqWorker>();
builder.Services.AddHostedService<BillingSagaReconciliationWorker>();
builder.Services.AddHttpClient<MercadoPagoClient>(http =>
{
    http.BaseAddress = new Uri("https://api.mercadopago.com/");
    http.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (origins.Length > 0)
        policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();
app.UseExceptionHandler();
app.UseCors();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapControllers();
app.Run();

public partial class Program;
