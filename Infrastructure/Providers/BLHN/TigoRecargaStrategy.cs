using System.Text.Encodings.Web;
using System.Text.Json;
using LAFISE.TransactionsRouter.Application.Common.Interfaces;
using LAFISE.TransactionsRouter.Domain.ApiContract.Sofia;
using LAFISE.TransactionsRouter.Domain.Enums.BLHN;
using LAFISE.TransactionsRouter.Domain.Enums.Sofia;
using Lafiservicios.Domain.Core.Enums;
using Microsoft.Extensions.Logging;
using AS400Request = LAFISE.TransactionsRouter.Domain.ApiContract.AS400.Request;
using AS400Response = LAFISE.TransactionsRouter.Domain.ApiContract.AS400.Response;
using Request = LAFISE.TransactionsRouter.Domain.ApiContract.Lafiservicios.Request;
using Response = LAFISE.TransactionsRouter.Domain.ApiContract.Lafiservicios.Response;

namespace LAFISE.TransactionsRouter.Infrastructure.Providers.BLHN
{
    public class TigoRecargaStrategy(ILogger<TigoRecargaStrategy> logger) : IProviderStrategy
    {
        private readonly ILogger<TigoRecargaStrategy> _logger = logger;

        private readonly JsonSerializerOptions _encoderOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };

        const string Reference = "TNUDOCTRA";
        const string CurrencyField = "MONEDA";
        const string TotalAmount = "MONTOTOTAL";
        const string CellPhone = "NUMCELULAR";
        const string InternalProductType = "TIPOPRODUCTO";
        const string SelectorCode = "CODSELECTOR";
        const string ProductCode = "CODPRODUCTO";

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
            _logger.LogInformation("TigoRecargaStrategy => MakeRequest(). Starting to make debt request content.");

            var cellPhone = sofia.FindFieldValue<string>(CellPhone);
            if (string.IsNullOrWhiteSpace(cellPhone))
            {
                sofia.SetStatus(Status.Error, Errors.Required(CellPhone));
                return (false, null);
            }

            var productCode = sofia.FindFieldValue<string>(ProductCode);
            if (string.IsNullOrWhiteSpace(productCode))
            {
                sofia.SetStatus(Status.Error, Errors.Required(ProductCode));
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
                        Key = "NumeroCelular",
                        Type = "String",
                        Value = cellPhone
                    },
                    new()
                    {
                        Key = "CodigoProducto",
                        Type = "String",
                        Value = productCode
                    },
                    new()
                    {
                        Key = "CodigoSelector",
                        Type = "String",
                        Value = sofia.FindFieldValue<string>(SelectorCode)
                    },
                    new()
                    {
                        Key = "TipoProductoInterno",
                        Type = "String",
                        Value = sofia.FindFieldValue<string>(InternalProductType)
                    }
                ]
            });
        }

        public void HandleRequest(SofiaBase sofia, Response.Debt debtResponse)
        {
            _logger.LogInformation("TigoRecargaStrategy => HandleRequest(). Starting to handle debt response content.");

            var debt = debtResponse.OutstandingDebts[0];
            var additional = debt.AdditionalData!;

            sofia.SetFields(new Dictionary<string, object?>
            {
                { "IDPAGO", debtResponse.PaymentId },
                { CellPhone, debt.Id },
                { "DESCRIPCION", debt.Description },
                { TotalAmount, debt.Amount },
                { ProductCode, additional.GetValueOrDefault("CodigoProducto") },
                { SelectorCode, additional.GetValueOrDefault("CodigoSelector") },
                { InternalProductType, additional.GetValueOrDefault("TipoProductoInterno") },
                { "FORMAPAGO", additional.GetValueOrDefault("FormaPago") }
            });

            sofia.SetStatus(Status.Success, "CONSULTA EXITOSA");

            _logger.LogInformation("TigoRecargaStrategy => HandleRequest(). Finished handle debt response content.");
        }
        #endregion

        #region Payment
        public (bool, Request.Payment?) MakePayment(SofiaBase sofia, CancellationToken cancellationToken)
        {
            _logger.LogInformation("TigoRecargaStrategy => MakePayment(). Starting making payment request content.");

            var total = sofia.FindFieldValue<decimal>(TotalAmount);
            var payments = sofia.FindFieldValue<decimal>("MPAGOACT")
                                + sofia.FindFieldValue<decimal>("MONTOACUM")
                                + sofia.FindFieldValue<decimal>("MPAGOACT2");

            if (payments != total)
            {
                _logger.LogError("TigoRecargaStrategy => MakePayment(). Payment amounts does not match. Total: {TOTAL} and Payment: {PAYMENTS}.", total, payments);

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
                        Key = "NumeroCelular",
                        Type = "String",
                        Value = sofia.FindFieldValue<string>(CellPhone)
                    },
                    new()
                    {
                        Key = "CodigoProducto",
                        Type = "String",
                        Value = sofia.FindFieldValue<string>(ProductCode)
                    },
                    new()
                    {
                        Key = "CodigoSelector",
                        Type = "String",
                        Value = sofia.FindFieldValue<string>(SelectorCode)
                    },
                    new()
                    {
                        Key = "TipoProductoInterno",
                        Type = "String",
                        Value = sofia.FindFieldValue<string>(InternalProductType)
                    },
                    new()
                    {
                        Key = "FormaPago",
                        Type = "String",
                        Value = sofia.FindFieldValue<string>("FORMAPAGO")
                    }
                ],
                OutstandingDebts =
                [
                    new()
                    {
                        Id = sofia.FindFieldValue<string>(CellPhone),
                        Amount = sofia.FindFieldValue<decimal>(TotalAmount),
                        Currency = ((Currency)sofia.FindFieldValue<int>(CurrencyField)).ToString(),
                    }
                ]
            });
        }

        public async Task HandlePayment(IAS400Service as400, SofiaBase sofia, Response.Check paymentResponse)
        {
            _logger.LogInformation("TigoRecargaStrategy => HandlePayment(). Starting to handle payment response content.");

            #region Increase balance
            var balance = CreateBalanceRequest(sofia);
            var increase = await as400.IncreaseBalance(balance);

            if (!AS400Response.StoreProcedure.ValidateCoreResponse(increase))
            {
                _logger.LogError("TigoRecargaStrategy => HandlePayment(). Increase balance failed: {Response}", JsonSerializer.Serialize(increase.Data, _encoderOpts));

                sofia.SetStatus(Status.Error, Errors.IncreaseBalance);
                return;
            }
            #endregion

            #region Save AUDIT
            var save = CreateSaveRequest(sofia);
            var record = await as400.SaveAudit(save);

            if (!AS400Response.StoreProcedure.ValidateCoreResponse(record))
            {
                _logger.LogError("TigoRecargaStrategy => HandlePayment(). Save transaction on AUDIT failed: {Response}", JsonSerializer.Serialize(record.Data, _encoderOpts));

                sofia.SetStatus(Status.Error, Errors.SaveAudit);
                return;
            }
            #endregion

            var response = paymentResponse.DebtToPay![0].AdditionalData!;

            sofia
                .SetField("CHECKSUM", response.GetValueOrDefault("Checksum"))
                .SetField("REFPAGO", response.GetValueOrDefault("PaymentReference"));

            sofia.SetStatus(Status.Success, "RECARGA EXITOSA");

            _logger.LogInformation("TigoRecargaStrategy => HandlePayment(). Finished handle payment response content. Balance: {BALANCE} and Save: {SAVE} executions.",
                JsonSerializer.Serialize(increase.Data, _encoderOpts), JsonSerializer.Serialize(record.Data, _encoderOpts));
        }
        #endregion

        #region Reverse
        public Request.Reverse MakeReverse(SofiaBase sofia, CancellationToken cancellationToken)
        {
            _logger.LogInformation("TigoRecargaStrategy => MakeReverse(). Starting making reverse request content.");

            return new Request.Reverse
            {
                PaymentId = sofia.FindFieldValue<string>("IDPAGO")
            };
        }

        public async Task HandleReverse(IAS400Service as400, SofiaBase sofia)
        {
            _logger.LogInformation("TigoRecargaStrategy => HandleReverse(). Starting to handle reverse response content.");

            #region Decrease balance
            var balance = CreateBalanceRequest(sofia);
            var decrease = await as400.DecreaseBalance(balance);

            if (!AS400Response.StoreProcedure.ValidateCoreResponse(decrease))
            {
                _logger.LogError("TigoRecargaStrategy => HandleReverse(). Decrease balance failed: {Response}", JsonSerializer.Serialize(decrease.Data, _encoderOpts));

                sofia.SetStatus(Status.Error, Errors.DecreaseBalance);
                return;
            }
            #endregion

            #region Reverse AUDIT
            var reverse = CreateReverseRequest(sofia);
            var record = await as400.ReverseAudit(reverse);

            if (!AS400Response.StoreProcedure.ValidateCoreResponse(record))
            {
                _logger.LogError("TigoRecargaStrategy => HandleReverse(). Reverse transaction on AUDIT failed: {Response}", JsonSerializer.Serialize(record.Data, _encoderOpts));

                sofia.SetStatus(Status.Error, Errors.ReverseAudit);
                return;
            }
            #endregion

            sofia.SetStatus(Status.Success, "REVERSION EXITOSA");

            _logger.LogInformation("TigoRecargaStrategy => HandleReverse(). Finished handle reverse response content. Balance: {BALANCE} and Reverse: {REVERSE} executions.",
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

            _logger.LogInformation("TigoRecargaStrategy => CreateBalanceRequest(). Making request to update balance: {BALANCE}", JsonSerializer.Serialize(request, _encoderOpts));
            return request;
        }

        private AS400Request.ReverseAudit CreateReverseRequest(SofiaBase sofia)
        {
            var request = new AS400Request.ReverseAudit
            {
                Transaction = sofia.TransactionNumber,
                Reference = sofia.FindFieldValue<string>(Reference)
            };

            _logger.LogInformation("TigoRecargaStrategy => CreateReverseRequest(). Making request to reverse in AUDIT: {RECORD}", JsonSerializer.Serialize(request, _encoderOpts));
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
                Description = $"RECARGA TIGO {sofia.FindFieldValue<string>(CellPhone)}",
                TotalAmount = sofia.FindFieldValue<decimal>(TotalAmount),
                Reference = sofia.FindFieldValue<string>(Reference),
                OtherReference = sofia.FindFieldValue<string>(CellPhone),
                Comision = sofia.FindFieldValue<decimal>("COMISION"),
                Branch = sofia.Branch
            };

            _logger.LogInformation("TigoRecargaStrategy => CreateSaveRequest(). Making request to save in AUDIT: {RECORD}", JsonSerializer.Serialize(request, _encoderOpts));
            return request;
        }
        #endregion
    }
}
