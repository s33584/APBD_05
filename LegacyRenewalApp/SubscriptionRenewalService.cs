using System;

namespace LegacyRenewalApp
{
    public class SubscriptionRenewalService
    {
        public RenewalInvoice CreateRenewalInvoice(
            int customerId,
            string planCode,
            int seatCount,
            string paymentMethod,
            bool includePremiumSupport,
            bool useLoyaltyPoints)
        {
            ValidateInputs(customerId, planCode, seatCount, paymentMethod);

            string normalizedPlanCode = NormalizeCode(planCode);
            string normalizedPaymentMethod = NormalizeCode(paymentMethod);

            var customer = GetCustomer(customerId);
            var plan = GetPlan(normalizedPlanCode);
            EnsureCustomerIsActive(customer);

            decimal baseAmount = (plan.MonthlyPricePerSeat * seatCount * 12m) + plan.SetupFee;
            string notes = string.Empty;
            decimal discountAmount = CalculateDiscountAmount(customer, plan, seatCount, baseAmount, useLoyaltyPoints, ref notes);

            decimal subtotalAfterDiscount = baseAmount - discountAmount;
            if (subtotalAfterDiscount < 300m)
            {
                subtotalAfterDiscount = 300m;
                notes += "minimum discounted subtotal applied; ";
            }

            decimal supportFee = CalculateSupportFee(includePremiumSupport, normalizedPlanCode, ref notes);
            decimal paymentFee = CalculatePaymentFee(normalizedPaymentMethod, subtotalAfterDiscount, supportFee, ref notes);

            decimal taxRate = GetTaxRate(customer.Country);

            decimal taxBase = subtotalAfterDiscount + supportFee + paymentFee;
            decimal taxAmount = taxBase * taxRate;
            decimal finalAmount = CalculateFinalAmount(taxBase, taxAmount, ref notes);

            var invoice = new RenewalInvoice
            {
                InvoiceNumber = $"INV-{DateTime.UtcNow:yyyyMMdd}-{customerId}-{normalizedPlanCode}",
                CustomerName = customer.FullName,
                PlanCode = normalizedPlanCode,
                PaymentMethod = normalizedPaymentMethod,
                SeatCount = seatCount,
                BaseAmount = Math.Round(baseAmount, 2, MidpointRounding.AwayFromZero),
                DiscountAmount = Math.Round(discountAmount, 2, MidpointRounding.AwayFromZero),
                SupportFee = Math.Round(supportFee, 2, MidpointRounding.AwayFromZero),
                PaymentFee = Math.Round(paymentFee, 2, MidpointRounding.AwayFromZero),
                TaxAmount = Math.Round(taxAmount, 2, MidpointRounding.AwayFromZero),
                FinalAmount = Math.Round(finalAmount, 2, MidpointRounding.AwayFromZero),
                Notes = notes.Trim(),
                GeneratedAt = DateTime.UtcNow
            };

            LegacyBillingGateway.SaveInvoice(invoice);

            if (!string.IsNullOrWhiteSpace(customer.Email))
            {
                string subject = "Subscription renewal invoice";
                string body =
                    $"Hello {customer.FullName}, your renewal for plan {normalizedPlanCode} " +
                    $"has been prepared. Final amount: {invoice.FinalAmount:F2}.";

                LegacyBillingGateway.SendEmail(customer.Email, subject, body);
            }

            return invoice;
        }

        private static void ValidateInputs(int customerId, string planCode, int seatCount, string paymentMethod)
        {
            if (customerId <= 0)
            {
                throw new ArgumentException("Customer id must be positive");
            }

            if (string.IsNullOrWhiteSpace(planCode))
            {
                throw new ArgumentException("Plan code is required");
            }

            if (seatCount <= 0)
            {
                throw new ArgumentException("Seat count must be positive");
            }

            if (string.IsNullOrWhiteSpace(paymentMethod))
            {
                throw new ArgumentException("Payment method is required");
            }
        }

        private static string NormalizeCode(string input)
        {
            return input.Trim().ToUpperInvariant();
        }

        private static Customer GetCustomer(int customerId)
        {
            var customerRepository = new CustomerRepository();
            return customerRepository.GetById(customerId);
        }

        private static SubscriptionPlan GetPlan(string normalizedPlanCode)
        {
            var planRepository = new SubscriptionPlanRepository();
            return planRepository.GetByCode(normalizedPlanCode);
        }

        private static void EnsureCustomerIsActive(Customer customer)
        {
            if (!customer.IsActive)
            {
                throw new InvalidOperationException("Inactive customers cannot renew subscriptions");
            }
        }

        private static decimal CalculateDiscountAmount(
            Customer customer,
            SubscriptionPlan plan,
            int seatCount,
            decimal baseAmount,
            bool useLoyaltyPoints,
            ref string notes)
        {
            decimal discountAmount = 0m;

            if (customer.Segment == "Silver")
            {
                discountAmount += baseAmount * 0.05m;
                notes += "silver discount; ";
            }
            else if (customer.Segment == "Gold")
            {
                discountAmount += baseAmount * 0.10m;
                notes += "gold discount; ";
            }
            else if (customer.Segment == "Platinum")
            {
                discountAmount += baseAmount * 0.15m;
                notes += "platinum discount; ";
            }
            else if (customer.Segment == "Education" && plan.IsEducationEligible)
            {
                discountAmount += baseAmount * 0.20m;
                notes += "education discount; ";
            }

            if (customer.YearsWithCompany >= 5)
            {
                discountAmount += baseAmount * 0.07m;
                notes += "long-term loyalty discount; ";
            }
            else if (customer.YearsWithCompany >= 2)
            {
                discountAmount += baseAmount * 0.03m;
                notes += "basic loyalty discount; ";
            }

            if (seatCount >= 50)
            {
                discountAmount += baseAmount * 0.12m;
                notes += "large team discount; ";
            }
            else if (seatCount >= 20)
            {
                discountAmount += baseAmount * 0.08m;
                notes += "medium team discount; ";
            }
            else if (seatCount >= 10)
            {
                discountAmount += baseAmount * 0.04m;
                notes += "small team discount; ";
            }

            if (useLoyaltyPoints && customer.LoyaltyPoints > 0)
            {
                int pointsToUse = customer.LoyaltyPoints > 200 ? 200 : customer.LoyaltyPoints;
                discountAmount += pointsToUse;
                notes += $"loyalty points used: {pointsToUse}; ";
            }

            return discountAmount;
        }

        private static decimal CalculateSupportFee(bool includePremiumSupport, string normalizedPlanCode, ref string notes)
        {
            if (!includePremiumSupport)
            {
                return 0m;
            }

            decimal supportFee = 0m;
            if (normalizedPlanCode == "START")
            {
                supportFee = 250m;
            }
            else if (normalizedPlanCode == "PRO")
            {
                supportFee = 400m;
            }
            else if (normalizedPlanCode == "ENTERPRISE")
            {
                supportFee = 700m;
            }

            notes += "premium support included; ";
            return supportFee;
        }

        private static decimal CalculatePaymentFee(
            string normalizedPaymentMethod,
            decimal subtotalAfterDiscount,
            decimal supportFee,
            ref string notes)
        {
            decimal baseForPaymentFee = subtotalAfterDiscount + supportFee;

            if (normalizedPaymentMethod == "CARD")
            {
                notes += "card payment fee; ";
                return baseForPaymentFee * 0.02m;
            }

            if (normalizedPaymentMethod == "BANK_TRANSFER")
            {
                notes += "bank transfer fee; ";
                return baseForPaymentFee * 0.01m;
            }

            if (normalizedPaymentMethod == "PAYPAL")
            {
                notes += "paypal fee; ";
                return baseForPaymentFee * 0.035m;
            }

            if (normalizedPaymentMethod == "INVOICE")
            {
                notes += "invoice payment; ";
                return 0m;
            }

            throw new ArgumentException("Unsupported payment method");
        }

        private static decimal GetTaxRate(string country)
        {
            if (country == "Poland")
            {
                return 0.23m;
            }

            if (country == "Germany")
            {
                return 0.19m;
            }

            if (country == "Czech Republic")
            {
                return 0.21m;
            }

            if (country == "Norway")
            {
                return 0.25m;
            }

            return 0.20m;
        }

        private static decimal CalculateFinalAmount(decimal taxBase, decimal taxAmount, ref string notes)
        {
            decimal finalAmount = taxBase + taxAmount;
            if (finalAmount < 500m)
            {
                notes += "minimum invoice amount applied; ";
                return 500m;
            }

            return finalAmount;
        }
    }
}
