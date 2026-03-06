using System.Text.Encodings.Web;
using System.Text.Json;
using LAFISE.TransactionsRouter.Application.Common.Interfaces;
using LAFISE.TransactionsRouter.Domain.ApiContract.Lafiservicios.Common;
using LAFISE.TransactionsRouter.Domain.ApiContract.Lafiservicios.Request;
using LAFISE.TransactionsRouter.Domain.ApiContract.Sofia;
using LAFISE.TransactionsRouter.Domain.Enums.BLHN;
using LAFISE.TransactionsRouter.Domain.Enums.Sofia;
using Lafiservicios.Domain.Core.Common.Utils;
using Lafiservicios.Domain.Core.Enums;
using Microsoft.Extensions.Logging;
using AS400Request = LAFISE.TransactionsRouter.Domain.ApiContract.AS400.Request;
using AS400Response = LAFISE.TransactionsRouter.Domain.ApiContract.AS400.Response;
using Request = LAFISE.TransactionsRouter.Domain.ApiContract.Lafiservicios.Request;
using Response = LAFISE.TransactionsRouter.Domain.ApiContract.Lafiservicios.Response;

namespace LAFISE.TransactionsRouter.Infrastructure.Providers.BLHN
{
    public class EneeStrategy(ILogger<EneeStrategy> logger) : IProviderStrategy
    {
        private readonly ILogger<EneeStrategy> _logger = logger;

        private readonly JsonSerializerOptions _encoderOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

        const string Key = "CLAVE";
        const string Reference = "TNUDOCTRA";
        const string CurrencyField = "MONEDA";
        const string TotalAmount = "TOTALPAGAR";

        public AS400Request.Validation GetMainData(SofiaBase sofia)
        {
            return new AS400Request.Validation
            {
                Transaction = sofia.TransactionNumber ?? 0,
                Reference = sofia.FindFieldValue<int>(Reference),
                Teller = sofia.Teller!,
                Currency = ((Currency)sofia.FindFieldValue<int>(CurrencyField)).ToCurrency(),
                Terminal = sofia.FindFieldValue<string>("TERMSOFIA")
            };
        }

        #region Debt
        public (bool, Request.Debt?) MakeRequest(SofiaBase sofia, CancellationToken cancellationToken)
        {
            _logger.LogInformation("EneeStrategy => MakeRequest(). Starting to make debt request content.");

            var keyValue = sofia.FindFieldValue<string>(Key);
            if (string.IsNullOrWhiteSpace(keyValue))
            {
                sofia.SetStatus(Status.Error, Errors.Required(Key));
                return (false, null);
            }

            return (true, new Request.Debt
            {
                Branch = sofia.Branch.ToString()!,
                CustomerId = 0,
                InputFields =
                [
                    new()
                    {
                        Key = "Key",
                        Type = "String",
                        Value = keyValue
                    }
                ]
            });
        }

        public void HandleRequest(SofiaBase sofia, Response.Debt debtResponse)
        {
            _logger.LogInformation("EneeStrategy => HandleRequest(). Starting to handle debt response content.");

            var debt = debtResponse.OutstandingDebts[0];
            var additional = debt.AdditionalData!;

            sofia.SetFields(new Dictionary<string, object?>
            {
                { "IDPAGO", debtResponse.PaymentId },
                { "CLAVE", debt.Id },
                { "NABONADO", debt.Description },
                { "CLAVESEC", debt.Reference },
                { TotalAmount, debt.Amount },
                { "FECLECACT", debt.DueDate },
                { "DIRABONADO", additional.GetValueOrDefault("Address") },
                { "MULTIPLO", additional.GetValueOrDefault("Multiple") },
                { "CONTADOR", additional.GetValueOrDefault("Counter") },
                { "CONSKWH", additional.GetValueOrDefault("Usage") },
                { "TARIFA", additional.GetValueOrDefault("RateCode") },
                { "LECACT", additional.GetValueOrDefault("CurrentReading") },
                { "LECANT", additional.GetValueOrDefault("PreviousReading") },
                { "DIASFACT", additional.GetValueOrDefault("BilledDays") },
                { "MMESFACT1", additional.GetValueOrDefault("IssueDate") },
                { "FECHAEMI", additional.GetValueOrDefault("IssueDate") },
                { "FECLECANT", additional.GetValueOrDefault("PreviousDate") },
                { "SALDOANT", additional.GetValueOrDefault("BalancePreviousMonth") },
                { "PAGOSPER", additional.GetValueOrDefault("PeriodCharges") },
                { "VALRECTIF", additional.GetValueOrDefault("RectificationAmount") },
                { "CARGOENERG", additional.GetValueOrDefault("EnergyAmount") },
                { "ALUMBPUB", additional.GetValueOrDefault("LightingAmount") },
                { "OTROSCARG", additional.GetValueOrDefault("OtherDebitsAndCredits") },
                { "SUBSIDIO", additional.GetValueOrDefault("Subsidy") },
                { "COBINTERES", additional.GetValueOrDefault("InteresAmount") },
                { "CARGODEMAN", additional.GetValueOrDefault("DemandAmount") },
                { "CARGOREACT", additional.GetValueOrDefault("ReactiveAmount") },
                { "CARGOVOLTA", additional.GetValueOrDefault("VoltageAmount") },
                { "TOTALSNISV", additional.GetValueOrDefault("SubtotalWithoutTax") },
                { "IMPUESTO", additional.GetValueOrDefault("TaxAmount") },
                { "REACTIVO", additional.GetValueOrDefault("Reactive") },
                { "NAVISO", additional.GetValueOrDefault("NumberNotice") },
                { "CICLOPAGOS", additional.GetValueOrDefault("Payments") },
                { "ULTIMOMES", additional.GetValueOrDefault("LastMonthBilled") },
                { "POTENCIA", additional.GetValueOrDefault("PowerFactor") },
                { "NIS", additional.GetValueOrDefault("SupplyIdentificationNumber") }
            });

            sofia.SetStatus(Status.Success, "CONSULTA EXITOSA");

            _logger.LogInformation("EneeStrategy => HandleRequest(). Finished handle debt response content.");
        }
        #endregion

        #region Settings
        public (bool, Setting?) MakeSettingRequest(SofiaBase sofia, CancellationToken cancellationToken)
        {
            _logger.LogInformation("EneeStrategy => MakeSettingRequest(). Starting to make setting request content.");

            return (true, new Request.Setting
            {
                Branch = sofia.Branch.ToString()!,
                CustomerId = 0
            });
        }

        public void HandleSettingRequest(SofiaBase sofia, Response.Setting settingResponse)
        {
            _logger.LogInformation("EneeStrategy => HandleSettingRequest(). Starting to handle setting response content.");

            sofia.SetFields(
                new Dictionary<string, object?> {
                    { "NUMFACTURA", "ENEE" },
                    {nameof(settingResponse.ServiceId),settingResponse.ServiceId },
                    {nameof(settingResponse.ServiceName),settingResponse.ServiceName },
                    {nameof(settingResponse.Description),settingResponse.Description},
                    {nameof(settingResponse.BankId),settingResponse.BankId},
                    {nameof(settingResponse.CategoryId),settingResponse.CategoryId},
                    {nameof(settingResponse.ProviderId),settingResponse.ProviderId},
                    {nameof(settingResponse.ProviderDescription),settingResponse.ProviderDescription},
                    {nameof(settingResponse.PaymentType),settingResponse.PaymentType},
                    {nameof(settingResponse.IsPartialPaymentAllowed),settingResponse.IsPartialPaymentAllowed},
                    {nameof(settingResponse.HasPackage),settingResponse.HasPackage},
                    {nameof(settingResponse.Subscribable),settingResponse.Subscribable}
                }
            );

            sofia.SetStatus(Status.Success, "CONSULTA EXITOSA");

            _logger.LogInformation("EneeStrategy => HandleSettingRequest(). Finished handle setting response content.");
        }
        #endregion

        #region Payment
        public (bool, Request.Payment?) MakePayment(SofiaBase sofia, CancellationToken cancellationToken)
        {
            _logger.LogInformation("EneeStrategy => MakePayment(). Starting making payment request content.");

            var total = sofia.FindFieldValue<decimal>(TotalAmount);
            var payments = sofia.FindFieldValue<decimal>("MPAGOACT")
                                + sofia.FindFieldValue<decimal>("MONTOACUM")
                                + sofia.FindFieldValue<decimal>("MPAGOACT2");

            if (payments != total)
            {
                _logger.LogError("EneeStrategy => MakePayment(). Payment amounts does not match. Total: {TOTAL} and Payment: {PAYMENTS}.", total, payments);

                sofia.SetStatus(Status.Error, Errors.AmountMatch);
                return (false, null);
            }

            return (true, new Request.Payment
            {
                Branch = sofia.Branch.ToString()!,
                PaymentId = sofia.FindFieldValue<string>("IDPAGO"),
                CustomerId = 0,
                PaymentMethod = PaymentMethod.ExternalAccounting,
                PaymentReference = sofia.FindFieldValue<int>(Reference),
                ExternalTransactionId = sofia.FindFieldValue<string>(Reference),
                DebitAccount = 0,
                DebitAccountCurrency = null,
                InputFields =
                [
                    new()
                    {
                        Key = "Key",
                        Type = "String",
                        Value = sofia.FindFieldValue<string>(Key)
                    }
                ],
                OutstandingDebts =
                [
                    new()
                    {
                        Id = sofia.FindFieldValue<string>(Key),
                        Amount = sofia.FindFieldValue<decimal>(TotalAmount),
                        Currency = ((Currency)sofia.FindFieldValue<int>(CurrencyField)).ToString(),
                    }
                ]
            });
        }

        public async Task HandlePayment(IAS400Service as400, SofiaBase sofia, Response.Check paymentResponse)
        {
            _logger.LogInformation("EneeStrategy => HandlePayment(). Starting to handle payment response content.");

            #region Increase balance
            var balance = CreateBalanceRequest(sofia);
            var increase = await as400.IncreaseBalance(balance);

            if (!AS400Response.StoreProcedure.ValidateCoreResponse(increase))
            {
                _logger.LogError("EneeStrategy => HandlePayment(). Increase balance failed: {Response}", JsonSerializer.Serialize(increase.Data, _encoderOpts));

                sofia.SetStatus(Status.Error, Errors.IncreaseBalance);
                return;
            }
            #endregion

            #region Save AUDIT
            var save = CreateSaveRequest(sofia);
            var record = await as400.SaveAudit(save);

            if (!AS400Response.StoreProcedure.ValidateCoreResponse(record))
            {
                _logger.LogError("EneeStrategy => HandlePayment(). Save transaction on AUDIT failed: {Response}", JsonSerializer.Serialize(record.Data, _encoderOpts));

                sofia.SetStatus(Status.Error, Errors.SaveAudit);
                return;
            }
            #endregion

            var response = paymentResponse.DebtToPay![0].AdditionalData!;

            sofia
                .SetField("CHECKSUM", response.GetValueOrDefault("Checksum"))
                .SetField("REFPAGO", response.GetValueOrDefault("PaymentReference"));

            sofia.SetStatus(Status.Success, "PAGO REALIZADO");

            _logger.LogInformation("EneeStrategy => HandlePayment(). Finished handle payment response content. Balance: {BALANCE} and Save: {SAVE} executions.",
                JsonSerializer.Serialize(increase.Data, _encoderOpts), JsonSerializer.Serialize(record.Data, _encoderOpts));
        }
        #endregion

        #region Reverse
        public Request.Reverse MakeReverse(SofiaBase sofia, CancellationToken cancellationToken)
        {
            _logger.LogInformation("EneeStrategy => MakeReverse(). Starting making reverse request content.");

            return new Request.Reverse
            {
                PaymentId = sofia.FindFieldValue<string>("IDPAGO")
            };
        }

        public async Task HandleReverse(IAS400Service as400, SofiaBase sofia)
        {
            _logger.LogInformation("EneeStrategy => HandleReverse(). Starting to handle reverse response content.");

            #region Decrease balance
            var balance = CreateBalanceRequest(sofia);
            var decrease = await as400.DecreaseBalance(balance);

            if (!AS400Response.StoreProcedure.ValidateCoreResponse(decrease))
            {
                _logger.LogError("EneeStrategy => HandleReverse(). Decrease balance failed: {Response}", JsonSerializer.Serialize(decrease.Data, _encoderOpts));

                sofia.SetStatus(Status.Error, Errors.DecreaseBalance);
                return;
            }
            #endregion

            #region Reverse AUDIT
            var reverse = CreateReverseRequest(sofia);
            var record = await as400.ReverseAudit(reverse);

            if (!AS400Response.StoreProcedure.ValidateCoreResponse(record))
            {
                _logger.LogError("EneeStrategy => HandleReverse(). Reverse transaction on AUDIT failed: {Response}", JsonSerializer.Serialize(record.Data, _encoderOpts));

                sofia.SetStatus(Status.Error, Errors.ReverseAudit);
                return;
            }
            #endregion

            sofia.SetStatus(Status.Success, "REVERSION EXITOSA");

            _logger.LogInformation("EneeStrategy => HandleReverse(). Finished handle reverse response content. Balance: {BALANCE} and Reverse: {REVERSE} executions.",
                JsonSerializer.Serialize(decrease.Data, _encoderOpts), JsonSerializer.Serialize(record.Data, _encoderOpts));
        }
        #endregion

        #region Private methods
        private AS400Request.Balance CreateBalanceRequest(SofiaBase sofia)
        {
            var request = new AS400Request.Balance
            {
                Teller = sofia.Teller,
                Currency = ((Currency)sofia.FindFieldValue<int>(CurrencyField)).ToCurrency(),
                Cash = sofia.FindFieldValue<decimal>("MPAGOACT") + sofia.FindFieldValue<decimal>("MONTOACUM"),
            };

            _logger.LogInformation("EneeStrategy => CreateBalanceRequest(). Making request to update balance: {BALANCE}", JsonSerializer.Serialize(request, _encoderOpts));
            return request;
        }

        private AS400Request.ReverseAudit CreateReverseRequest(SofiaBase sofia)
        {
            var request = new AS400Request.ReverseAudit
            {
                Transaction = sofia.TransactionNumber,
                Reference = sofia.FindFieldValue<string>(Reference)
            };

            _logger.LogInformation("EneeStrategy => CreateReverseRequest(). Making request to reverse in AUDIT: {RECORD}", JsonSerializer.Serialize(request, _encoderOpts));
            return request;
        }

        private AS400Request.SaveAudit CreateSaveRequest(SofiaBase sofia)
        {
            var request = new AS400Request.SaveAudit
            {
                Teller = sofia.Teller,
                Currency = ((Currency)sofia.FindFieldValue<int>(CurrencyField)).ToCurrency(),
                Secuencial = sofia.Secuencial,
                Cash = sofia.FindFieldValue<decimal>("MPAGOACT") + sofia.FindFieldValue<decimal>("MONTOACUM"),
                LocalCheck = sofia.FindFieldValue<decimal>("MPAGOACT2"),
                NonlocalCheck = 0,
                Transaction = sofia.TransactionNumber,
                DebitAccount = 0,
                CreditAccount = 0,
                Description = $"PAGO EEH {sofia.FindFieldValue<string>(Key)}",
                TotalAmount = sofia.FindFieldValue<decimal>(TotalAmount),
                Reference = sofia.FindFieldValue<string>(Reference),
                OtherReference = sofia.FindFieldValue<string>(Key),
                Comision = 0,
                Branch = sofia.Branch
            };

            _logger.LogInformation("EneeStrategy => CreateSaveRequest(). Making request to save in AUDIT: {RECORD}", JsonSerializer.Serialize(request, _encoderOpts));
            return request;
        }
        #endregion
    }
}
