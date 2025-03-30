using FluentFTP;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Net;
using System.Globalization;
using System.Linq;

public class Program
{

    private static readonly string FtpHost = Environment.GetEnvironmentVariable("FTP_HOST");
    private static readonly string FtpUser = Environment.GetEnvironmentVariable("FTP_USER");
    private static readonly string FtpPass = Environment.GetEnvironmentVariable("FTP_PASS");
    private static readonly string BigCommerceApiUrl = Environment.GetEnvironmentVariable("BIGCOMMERCE_API_URL");
    private static readonly string BigCommerceToken = Environment.GetEnvironmentVariable("BIGCOMMERCE_TOKEN");
    private const string FtpFilePath = "/Stock.txt";

    static async Task Main()
    {
        var program = new Program();
        await program.RunUpdateAsync();
    }

    public async Task RunUpdateAsync()
    {
        try
        {
            string filePath = DownloadFileFromFTP();
            var productGroups = ReadProductsFromTxt(filePath);

            Console.WriteLine("Starting inventory update process...");

            foreach (var productGroup in productGroups)
            {
                var productDetail = await GetProductIdByName(productGroup.Name);
                if (productDetail != null)
                {
                    await UpdateProductInventoryInBigCommerce(productDetail.ProductId, productGroup.Variants);
                }
                else
                {
                    await CreateProductWithVariantsInBigCommerce(productGroup);
                }
            }

            Console.WriteLine("Inventory update process completed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in inventory update: {ex.Message}");
        }
    }

    public string DownloadFileFromFTP()
    {
        string localPath = Path.Combine(Path.GetTempPath(), "stock.txt");

        try
        {
            using (var ftpClient = new FtpClient(FtpHost, new NetworkCredential(FtpUser, FtpPass)))
            {
                ftpClient.Config.EncryptionMode = FtpEncryptionMode.None;
                ftpClient.Config.ValidateAnyCertificate = true;

                ftpClient.Connect();
                Console.WriteLine("FTP connection established");

                if (ftpClient.FileExists(FtpFilePath))
                {
                    ftpClient.DownloadFile(localPath, FtpFilePath);
                    Console.WriteLine("TXT file downloaded successfully.");
                }
                else
                {
                    Console.WriteLine("The file does not exist on the server.");
                }

                ftpClient.Disconnect();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }

        return localPath;
    }

    public List<ProductGroup> ReadProductsFromTxt(string filePath)
    {
        var groupedProducts = new Dictionary<string, ProductGroup>();

        try
        {
            foreach (var line in File.ReadAllLines(filePath))
            {
                var parts = line.Split('|');
                if (parts.Length < 10) continue;

                int inventory = int.Parse(parts[8]);
                if (inventory <= 0) continue; // Ignore products without stock

                var sku = parts[0];
                var ean = parts[1];
                var name = parts[2];
                var brand = parts[3];
                var category = parts[4];
                var gender = parts[5];
                var color = parts[6];
                var size = parts[7];
                var price = Convert.ToDecimal(parts[9].Replace(",", "."), CultureInfo.InvariantCulture);

                string parentKey = $"{ean}_{color}"; // Unique key based on EAN and Color

                if (!groupedProducts.ContainsKey(parentKey))
                {
                    groupedProducts[parentKey] = new ProductGroup
                    {
                        Name = $"{name} {color}", // Ensure unique product names
                        Sku = sku,
                        Price = price,
                        Variants = new List<ProductVariant>(),
                        inventory_tracking = "variant",
                        upc = ean,
                        is_visible = false,
                    };
                }

                // Ensure variant SKU is unique (avoid parent SKU conflict)
                string uniqueVariantSku = sku;
                if (sku == groupedProducts[parentKey].Sku)
                {
                    uniqueVariantSku = ModifySku(sku);
                }

                // Add the variant details
                groupedProducts[parentKey].Variants.Add(new ProductVariant
                {
                    Sku = uniqueVariantSku, // Unique SKU for variant
                    Price = price,
                    upc = ean,
                    inventory_level = inventory,
                    option_values = new List<ProductOption>
                {
                    new ProductOption { label = size, option_id = 0, option_display_name = "Size" }
                }
                });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing a product: {ex.Message}");
        }

        Console.WriteLine($"Organized {groupedProducts.Count} products into parent-child structures.");
        return groupedProducts.Values.ToList();
    }

    public async Task CreateProductWithVariantsInBigCommerce(ProductGroup productGroup)
    {
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("X-Auth-Token", BigCommerceToken);

            var productData = new
            {
                name = productGroup.Name,
                type = "physical",
                price = productGroup.Price,
                weight = 0,
                sku = productGroup.Sku, 
                inventory_tracking = "variant",
                is_visible = false,
                upc = productGroup.upc,
                variants = productGroup.Variants.Select(variant => new
                {
                    price = variant.Price,
                    weight = 0.1,
                    sku = variant.Sku,
                    upc = variant.upc,
                    inventory_level = variant.inventory_level,
                    option_values = new List<object>
                    {
                        new
                        {
                            label = variant.option_values[0].label,
                            option_id = 0,
                            option_display_name = "Size"
                        }
                    }
                }).ToList()
            };


            var json = JsonConvert.SerializeObject(productData, Formatting.Indented);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(BigCommerceApiUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error creating product: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                return;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            dynamic productResponse = JsonConvert.DeserializeObject(responseContent);
            int productId = productResponse.data.id;

            Console.WriteLine($"Product '{productGroup.Name}' created successfully with ID {productId}");
        }
    }

    //Helpers 
    
    //Generates a random SKU by appending a unique number
    private string GenerateRandomSku()
    {
        Random random = new Random();
        return random.Next(1000, 9999).ToString();
    }


    public async Task CreateVariantsInBigCommerce(int productId, List<Product> variants)
    {
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("X-Auth-Token", BigCommerceToken);

            foreach (var variant in variants)
            {
                var variantData = new
                {
                    product_id = productId,
                    sku = variant.Sku,
                    price = variant.Price,
                    weight = 0.1,
                    inventory_level = variant.Inventory,
                    option_values = new[]
                    {
                        new
                        {
                            label = variant.Size,
                            option_display_name = "Size"
                        }
                    }
                };

                var json = JsonConvert.SerializeObject(variantData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PostAsync($"{BigCommerceApiUrl}/{productId}/variants", content);
                if (response.IsSuccessStatusCode)
                    Console.WriteLine($"Variant {variant.Size} created for {productId}");
                else
                    Console.WriteLine($"Error creating variant: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
            }
        }
    }

    public async Task<ProductDetail?> GetProductIdByName(string name)
    {
        try
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("X-Auth-Token", BigCommerceToken);

                string url = $"{BigCommerceApiUrl}?name={WebUtility.UrlEncode(name)}&limit=1";
                var response = await client.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Error searching product: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                    return null;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var data = JsonConvert.DeserializeObject<dynamic>(responseContent);

                if (data?.data == null || data?.data.Count == 0)
                {
                    return null;
                }

                return new ProductDetail { ProductId = (int)data?.data[0].id };
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error getting product ID: {ex.Message}");
            return null;
        }
    }

    public async Task UpdateProductInventoryInBigCommerce(int productId, List<ProductVariant> variants)
    {
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("X-Auth-Token", BigCommerceToken);

            foreach (var variant in variants)
            {
                var variantData = new
                {
                    inventory_level = variant.inventory_level
                };

                var json = JsonConvert.SerializeObject(variantData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await client.PutAsync($"{BigCommerceApiUrl}/variants/{variant.Sku}", content);

                if (response.IsSuccessStatusCode)
                    Console.WriteLine($" Inventory updated for SKU {variant.Sku}");
                else
                    Console.WriteLine($" Error updating inventory: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
            }
        }
    }

    private string ModifySku(string originalSku)
    {
        if (string.IsNullOrEmpty(originalSku)) return originalSku;

        char lastChar = originalSku[^1];
        char newChar;

        do
        {
            newChar = (char)('0' + new Random().Next(0, 10));
        } while (newChar == lastChar);

        return originalSku.Substring(0, originalSku.Length - 1) + newChar;
    }


    public class Product
    {
        public string Sku { get; set; }
        public string Ean { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }
        public string Size { get; set; }
        public int Inventory { get; set; }
        public decimal Price { get; set; }
        public string upc { get; set; }
        public string Type { get; set; }
    }

    public class ProductDetail
    {
        public int ProductId { get; set; }
    }

    public class ProductGroup
    {
        public string Name { get; set; }
        public string Sku { get; set; }
        public decimal Price { get; set; }
        public string upc { get; set; }
        public List<ProductVariant> Variants { get; set; }
        public string inventory_tracking { get; set; }
        
        public Boolean is_visible { get; set; }

    }

    public class ProductVariant
    {
        public string Sku { get; set; }
        public decimal Price { get; set; }
        public string upc { get; set; }

        public int inventory_level { get; set; }
        public List<ProductOption> option_values { get; set; }
    }

    public class ProductOption
    {
        public string label { get; set; }
        public int option_id { get; set; }
        public string option_display_name { get; set; }
    }


}
