using System.Net;
using Billing.Api.Integrations;
using Billing.Api.Messaging;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Billing.Api.Infrastructure;

public sealed class IntegrationExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, detail) = exception switch
        {
            MessagingUnavailableException messaging => (503, messaging.Message),
            IntegrationConfigurationException config => (503, config.Message),
            MercadoPagoException { StatusCode: HttpStatusCode.NotFound } => (404, "Cobrança não encontrada no Mercado Pago."),
            MercadoPagoException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity } =>
                (422, "O Mercado Pago recusou os dados. Confira os dados do pagador e da cobrança."),
            MercadoPagoException { StatusCode: HttpStatusCode.Conflict } =>
                (409, "Conflito na operação. Consulte a cobrança antes de tentar criar outra."),
            MercadoPagoException { StatusCode: HttpStatusCode.TooManyRequests } =>
                (503, "Limite de chamadas atingido. Aguarde antes de tentar novamente."),
            MercadoPagoException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } =>
                (502, "O Mercado Pago recusou a credencial ou a permissão da aplicação."),
            MercadoPagoException => (502, "O Mercado Pago não retornou uma resposta válida. Tente novamente mais tarde."),
            HttpRequestException => (502, "Não foi possível conectar ao Mercado Pago."),
            OperationCanceledException when !ct.IsCancellationRequested =>
                (504, "O Mercado Pago demorou a responder. Reutilize a mesma chave de idempotência ao repetir a criação."),
            _ => (0, "")
        };
        if (status == 0) return false;

        context.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails { Status = status, Title = "Falha na integração de pagamento", Detail = detail }
        });
    }
}
