using Autofac.Extensions.DependencyInjection;
using CoreCms.Net.Loging;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog.Web;
using System;
using System.Linq;
using Autofac;
using CoreCms.Net.Auth;
using CoreCms.Net.Configuration;
using CoreCms.Net.Core.AutoFac;
using CoreCms.Net.Core.Config;
using CoreCms.Net.Filter;
using CoreCms.Net.Mapping;
using CoreCms.Net.Middlewares;
using CoreCms.Net.Swagger;
using CoreCms.Net.Web.Admin.Infrastructure;
using Essensoft.Paylink.Alipay;
using Essensoft.Paylink.WeChatPay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Yitter.IdGenerator;


var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsProduction())
    {
        var checks = new (string Key, string Label, int MinLen)[]
        {
            ("JwtConfig:SecretKey", "JwtConfig:SecretKey (需 >=16 字符)", 16),
            ("JwtConfig:Issuer", "JwtConfig:Issuer", 1),
        };
        var missingList = checks
            .Where(c => string.IsNullOrWhiteSpace(builder.Configuration[c.Key]) || builder.Configuration[c.Key]!.Length < c.MinLen)
            .Select(c => c.Label).ToList();
        if (missingList.Count > 0)
            throw new InvalidOperationException($"生产环境缺少关键配置，请在 appsettings.json 或环境变量中填写: {string.Join(", ", missingList)}");
    }

//���ӱ���·����ȡ֧��
builder.Services.AddSingleton(new AppSettingsHelper(builder.Environment.ContentRootPath));
builder.Services.AddSingleton(new LogLockHelper(builder.Environment.ContentRootPath));

//Memory����
builder.Services.AddMemoryCacheSetup();
//Redis����
builder.Services.AddRedisCacheSetup();

//�������ݿ�����SqlSugarע��֧��
builder.Services.AddSqlSugarSetup();
//���ÿ���CORS��
builder.Services.AddCorsSetup();

//����session֧��(session������cache���д洢)
builder.Services.AddSession();
// AutoMapper֧��
// AutoMapper֧�֣�15�汾����������Ȩģʽ����ҿ���ǰ��https://luckypennysoftware.com/�����Լ���ѵ�key��
//builder.Services.AddAutoMapper(typeof(AutoMapperConfiguration));
builder.Services.AddAutoMapper(typeof(AutoMapperConfiguration));

//ʹ�� SignalR
builder.Services.AddSignalR();

// ����Payment ����ע��(֧����֧��/΢��֧��)
builder.Services.AddAlipay();
builder.Services.AddWeChatPay();

// �� appsettings.json �� ����ѡ��
builder.Services.Configure<WeChatPayOptions>(builder.Configuration.GetSection("WeChatPay"));
builder.Services.Configure<AlipayOptions>(builder.Configuration.GetSection("Alipay"));

//ע���Զ���΢�Žӿ������ļ�
builder.Services.Configure<CoreCms.Net.WeChat.Service.Options.WeChatOptions>(builder.Configuration.GetSection(nameof(CoreCms.Net.WeChat.Service.Options.WeChatOptions)));

// ע�빤�� HTTP �ͻ���
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CoreCms.Net.WeChat.Service.HttpClients.IWeChatApiHttpClientFactory, CoreCms.Net.WeChat.Service.HttpClients.WeChatApiHttpClientFactory>();

//Swagger�ӿ��ĵ�ע��
builder.Services.AddAdminSwaggerSetup();

//���������ƴ�ӡ��
builder.Services.AddYiLianYunSetup();

//jwt��Ȩ֧��ע��
builder.Services.AddAuthorizationSetupForAdmin();
//������ע��
builder.Services.AddHttpContextSetup();

//���������м���AutoFac�������滻����
builder.Services.Replace(ServiceDescriptor.Transient<IControllerActivator, ServiceBasedControllerActivator>());

//ע��mvc��ע��razor������ͼ
builder.Services.AddMvc(options =>
{
    //ʵ����֤
    options.Filters.Add<RequiredErrorForAdmin>();
    //�쳣����
    options.Filters.Add<GlobalExceptionsFilterForAdmin>();
    //Swagger�޳�����Ҫ����apiչʾ���б�
    options.Conventions.Add(new ApiExplorerIgnores());

    options.EnableEndpointRouting = false;
})
    .AddNewtonsoftJson(p =>
    {
        //���ݸ�ʽ����ĸСд ��ʹ���շ�
        p.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
        //��ʹ���շ���ʽ��key
        //p.SerializerSettings.ContractResolver = new DefaultContractResolver();
        //����ѭ������
        p.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        //����ʱ���ʽ
        p.SerializerSettings.DateFormatString = "yyyy-MM-dd HH:mm:ss";
    });


// ѩ��Ư���㷨
// ���� IdGeneratorOptions �������ڹ��캯�������� WorkerId��
var options = new IdGeneratorOptions(1);
// �������������Ĳ����������������ö�������Ч����
YitIdHelper.SetIdGenerator(options);

#region AutoFacע��============================================================================

builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory());
builder.Host.ConfigureContainer<ContainerBuilder>(containerBuilder =>
{
    //��ȡ���п��������Ͳ�ʹ������ע��
    var controllerBaseType = typeof(ControllerBase);
    containerBuilder.RegisterAssemblyTypes(typeof(Program).Assembly)
        .Where(t => controllerBaseType.IsAssignableFrom(t) && t != controllerBaseType)
        .PropertiesAutowired();

    containerBuilder.RegisterModule(new AutofacModuleRegister());

});

#endregion

var app = builder.Build();

#region ���Ubuntu Nginx �������ܻ�ȡIP����
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
#endregion

// ��¼�����뷵������ (ע�⿪��Ȩ�ޣ���Ȼ�����޷�д��)
app.UseRequestResponseLog();
// �û����ʼ�¼(����ŵ���㣬��Ȼ��������쳣���ᱨ������Ϊ���ܷ�����)(ע�⿪��Ȩ�ޣ���Ȼ�����޷�д��)
app.UseRecordAccessLogsMildd();
// ��¼ip���� (ע�⿪��Ȩ�ޣ���Ȼ�����޷�д��)
app.UseIpLogMildd();

app.UseSwagger().UseSwaggerUI(c =>
{
    //���ݰ汾���Ƶ��� ����չʾ
    typeof(CustomApiVersion.ApiVersions).GetEnumNames().OrderByDescending(e => e).ToList().ForEach(
        version =>
        {
            c.SwaggerEndpoint($"/swagger/{version}/swagger.json", $"Doc {version}");
        });
    c.RoutePrefix = "doc";
});

//ʹ�� Session
app.UseSession();

if (app.Environment.IsDevelopment())
{
    // �ڿ��������У�ʹ���쳣ҳ�棬�������Ա�¶�����ջ��Ϣ�����Բ�Ҫ��������������
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// CORS����
app.UseCors(AppSettingsConstVars.CorsPolicyName);
// ��תhttps
//app.UseHttpsRedirection();
// ʹ�þ�̬�ļ�
app.UseStaticFiles();
// ʹ��cookie
app.UseCookiePolicy();
// ���ش�����
app.UseStatusCodePages();
// Routing
app.UseRouting();
// �ȿ�����֤
app.UseAuthentication();
// Ȼ������Ȩ�м��
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

//����Ĭ����ʼҳ����default.html��
//�˴���·���������wwwroot�ļ��е����·��
var defaultFilesOptions = new DefaultFilesOptions();
defaultFilesOptions.DefaultFileNames.Clear();
defaultFilesOptions.DefaultFileNames.Add("index.html");
app.UseDefaultFiles(defaultFilesOptions);
app.UseStaticFiles();

try
{
    //ȷ��NLog.config�������ַ�����appsettings.json��ͬ��
    NLogUtil.EnsureNlogConfig("NLog.config");
    //������Ŀ����ʱ��Ҫ��������
    NLogUtil.WriteAll(NLog.LogLevel.Trace, LogType.ApiRequest, "�ӿ�����", "�ӿ������ɹ�");

    app.Run();
}
catch (Exception ex)
{
    //ʹ��Nlogд��������־�ļ�����һ���ݿ�û����/���ӳɹ���
    NLogUtil.WriteFileLog(NLog.LogLevel.Error, LogType.ApiRequest, "�ӿ�����", "��ʼ�������쳣", ex);
    throw;
}
