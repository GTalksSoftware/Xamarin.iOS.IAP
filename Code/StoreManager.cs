using System;
using System.Collections.Generic;
using System.Linq;
using MonoTouch.Foundation;
using MonoTouch.StoreKit;

namespace FlowChaser
{
    public enum ProductState
    {
        Unknown,
        Invalid,
        Loaded,
        Purchasing,
        Purchased
    }

    public interface IProductDetails
    {
        ProductState State { get; }

        string GetLocalizedPrice();

        string GetLocalisedTitle();

        string GetLocalisedDescription();

        void Purchase();
    }

    /// <summary>
    /// Singleton class for managing app store comunication.
    /// </summary>
    /// <remarks>
    /// Inspired by:
    /// http://blog.fivelakesstudio.com/2013/04/ios-in-app-purchases.html#.UupN0Xd_t8z
    /// </remarks>
    public class StoreManager
    {
        private static readonly StoreManager SingleManager = new StoreManager();

        private readonly SKPaymentQueue PaymentQueue = SKPaymentQueue.DefaultQueue;
        private readonly ProductsRequestDelegate productsRequestDelegate = new ProductsRequestDelegate();
        private readonly PaymentObserver paymentObserver = new PaymentObserver();

        private readonly Dictionary<string, ProductDetails> products = new Dictionary<string, ProductDetails>();
        private SKProductsRequest productsRequest;

        private StoreManager()
        {
            foreach (var transaction in PaymentQueue.Transactions)
                PaymentQueue.FinishTransaction(transaction);

            PaymentQueue.AddTransactionObserver(this.paymentObserver);
        }

        public event EventHandler DetailsChanged;

        public event EventHandler<TransactionFailedEventArgs> TransactionFailed;

        /// <summary>
        /// Singleton instance accessor.
        /// </summary>
        /// <value>The store manager.</value>
        public static StoreManager Manager { get { return SingleManager; } }

        /// <summary>
        /// Gets a value indicating whether the user can make payments.
        /// </summary>
        /// <value><c>true</c> if the user can make payments; otherwise, <c>false</c>.</value>
        public bool CanMakePayments { get { return SKPaymentQueue.CanMakePayments; } }

        /// <summary>
        /// Registers the product identifiers the application is interested in with the store manager class.
        /// </summary>
        /// <param name="productIdentifiers">The product identifiers to register.</param>
        public void RegisterProducts(params string[] productIdentifiers)
        {
            foreach (var id in productIdentifiers)
            {
                if (!this.products.ContainsKey(id))
                {
                    var product = new ProductDetails(id);
                    this.products [id] = product;
                }
            }

            this.RequestProductDetails();
        }

        /// <summary>
        /// Gets the product details for the given product ID.
        /// </summary>
        /// <param name="id">The product identifier.</param>
        /// <returns>The product details.</returns>
        public IProductDetails GetProductDetails(string id)
        {
            ProductDetails details;
            if (this.products.TryGetValue(id, out details))
                return details;

            return null;
        }

        /// <summary>
        /// Determines whether the specified identifiers product is purchased.
        /// </summary>
        /// <returns>Whether the product is purchased.</returns>
        /// <param name="id">The product identifier.</param>
        public bool IsPurchased(string id)
        {
            var details = this.GetProductDetails(id);
            return details != null && details.State == ProductState.Purchased;
        }

        /// <summary>
        /// Restores the purchased products.
        /// </summary>
        public void RestorePurchased()
        {
            PaymentQueue.RestoreCompletedTransactions();
        }

        private void RequestProductDetails()
        {
            if (this.products.Values.All(o => o.HasValidCache() || o.State == ProductState.Invalid || o.State == ProductState.Purchased))
                return;

            // If an existing request is in then progress cancel it
            var currentRequest = this.productsRequest;
            if (currentRequest != null)
                currentRequest.Cancel();

            var productIdentifiers = NSSet.MakeNSObjectSet<NSString>(
                this.products.Where(o => o.Value.State == ProductState.Loaded || o.Value.State == ProductState.Unknown).Select(o => new NSString(o.Key)).ToArray());                        

            this.productsRequest = new SKProductsRequest(productIdentifiers);
            this.productsRequest.Delegate = this.productsRequestDelegate; // SKProductsRequestDelegate.ReceivedResponse
            this.productsRequest.Start();
        }

        private void RaiseDetailsChanged()
        {
            var handler = this.DetailsChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }

        private void RaiseTransactionFailed(NSError error)
        {
            var handler = this.TransactionFailed;
            if (handler != null)
                handler(this, new TransactionFailedEventArgs("Transaction Failed: Code=" + error.Code + " " + error.LocalizedDescription));
        }

        private class ProductsRequestDelegate : SKProductsRequestDelegate
        {
            public override void ReceivedResponse(SKProductsRequest request, SKProductsResponse response)
            {
                if (SingleManager.productsRequest == request)
                    SingleManager.productsRequest = null;

                var changed = false;
                foreach (var product in response.Products)
                {
                    ProductDetails details;
                    if (SingleManager.products.TryGetValue(product.ProductIdentifier, out details))
                    {
                        details.SetDetails(product);
                        changed = true;
                    }
                }

                foreach (var product in response.InvalidProducts)
                {
                    ProductDetails details;
                    if (SingleManager.products.TryGetValue(product, out details))
                    {
                        details.SetInvalid();
                        changed = true;
                    }
                }

                // Check that all products have details - if not reload
                SingleManager.RequestProductDetails();

                if (changed)
                    SingleManager.RaiseDetailsChanged();
            }

            public override void RequestFailed(SKRequest request, NSError error)
            {
                // Retry the load - most likley case is no network
                SingleManager.RequestProductDetails();
            }
        }

        private class PaymentObserver : SKPaymentTransactionObserver
        {
            public override void UpdatedTransactions(SKPaymentQueue queue, SKPaymentTransaction[] transactions)
            {
                var purchaseChanged = false;
                foreach (var transaction in transactions)
                {
                    switch (transaction.TransactionState)
                    {
                        case SKPaymentTransactionState.Purchased:
                            {
                                ProductDetails details;
                                if (SingleManager.products.TryGetValue(transaction.Payment.ProductIdentifier, out details))
                                {
                                    purchaseChanged = true;
                                    details.SetPurchased();
                                }

                                SingleManager.PaymentQueue.FinishTransaction(transaction);
                                break;
                            }
                        case SKPaymentTransactionState.Restored:
                            {
                                ProductDetails details;
                                if (SingleManager.products.TryGetValue(transaction.OriginalTransaction.Payment.ProductIdentifier, out details))
                                {
                                    purchaseChanged = true;
                                    details.SetPurchased();
                                }

                                SingleManager.PaymentQueue.FinishTransaction(transaction);
                                break;
                            }
                        case SKPaymentTransactionState.Failed:
                            {
                                ProductDetails details;
                                if (SingleManager.products.TryGetValue(transaction.Payment.ProductIdentifier, out details))
                                {
                                    purchaseChanged = true;
                                    details.SetFailed();
                                }

                                if ((SKError)transaction.Error.Code != SKError.PaymentCancelled)
                                    SingleManager.RaiseTransactionFailed(transaction.Error);

                                SingleManager.PaymentQueue.FinishTransaction(transaction);
                                break;
                            }
                        case SKPaymentTransactionState.Purchasing:
                            {
                                ProductDetails details;
                                if (SingleManager.products.TryGetValue(transaction.Payment.ProductIdentifier, out details))
                                {
                                    purchaseChanged = true;
                                    details.SetPurchasing();
                                }

                                break;
                            }
                    }
                }

                if (purchaseChanged)
                    SingleManager.RaiseDetailsChanged();
            }

            public override void RemovedTransactions(SKPaymentQueue queue, SKPaymentTransaction[] transactions)
            {
                foreach (var transaction in transactions)
                    SingleManager.PaymentQueue.FinishTransaction(transaction);
            }

            public override void RestoreCompletedTransactionsFailedWithError(SKPaymentQueue queue, NSError error) { }

            public override void PaymentQueueRestoreCompletedTransactionsFinished(SKPaymentQueue queue) { }
        }

        private class ProductDetails : IProductDetails
        {
            private readonly string iapKey;
            private SKProduct details;
            private DateTime lastDetailsRequest = DateTime.MinValue;

            public ProductDetails(string id)
            {
                this.iapKey = "IAP_" + id;
                var purchased = NSUserDefaults.StandardUserDefaults.BoolForKey(this.iapKey); // If key does not exist returns false (which is perfect for us)
                this.State = purchased ? ProductState.Purchased : ProductState.Unknown;
            }

            public ProductState State { get; private set; }

            public bool HasValidCache()
            {
                return DateTime.Now - this.lastDetailsRequest < TimeSpan.FromDays(1);
            }

            public void SetDetails(SKProduct productDetails)
            {
                this.lastDetailsRequest = DateTime.Now;
                this.details = productDetails;
                if (this.State == ProductState.Unknown)
                    this.State = ProductState.Loaded;
            }

            public void SetInvalid() { this.State = ProductState.Invalid; }

            public void SetFailed()
            {
                if (this.State == ProductState.Purchasing)
                    this.State = this.details == null ? ProductState.Unknown : ProductState.Loaded;
            }
        
            public void SetPurchasing() { this.State = ProductState.Purchasing; }

            public string GetLocalizedPrice()
            {
                if (this.details == null)
                    return null;

                var formatter = new NSNumberFormatter();
                formatter.FormatterBehavior = NSNumberFormatterBehavior.Version_10_4;  
                formatter.NumberStyle = NSNumberFormatterStyle.Currency;
                formatter.Locale = this.details.PriceLocale;

                return formatter.StringFromNumber(this.details.Price);
            }

            public string GetLocalisedTitle() { return this.details == null ? null : this.details.LocalizedTitle; }

            public string GetLocalisedDescription() { return this.details == null ? null : this.details.LocalizedDescription; }

            public void Purchase()
            {
                SKPayment payment = SKPayment.PaymentWithProduct(this.details);
                SingleManager.PaymentQueue.AddPayment(payment);
            }

            public void SetPurchased()
            {
                if (this.State == ProductState.Purchased)
                    return;

                this.State = ProductState.Purchased;

                // Whilst we could do something more secure than
                // using NSUserDefaults here it is simple and the
                // majority of people who would actually pay will
                // not be looking to try and hack it in the first place.
                NSUserDefaults.StandardUserDefaults.SetBool(true, this.iapKey);
                NSUserDefaults.StandardUserDefaults.Synchronize();
            }
        }
    }

    public class TransactionFailedEventArgs : EventArgs
    {
        public TransactionFailedEventArgs(string message)
        {
            this.Message = message;
        }

        public string Message { get; private set; }
    }
}

