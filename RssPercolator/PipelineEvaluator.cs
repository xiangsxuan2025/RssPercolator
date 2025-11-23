using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel.Syndication;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Net.Http;
using System.IO;

namespace RssPercolator
{
    /// <summary>
    /// Executes a pipeline. <remarks>Filters are always executed in a sequence from top to bottom.
    /// Make sure to put broad filters in the beginning.</remarks>
    /// </summary>
    public sealed class PipelineEvaluator : IPipelineEvaluator
    {
        public static IPipelineEvaluator Create()
        {
            return new PipelineEvaluator();
        }

        private PipelineEvaluator()
        {
            this.httpClient = new HttpClient();
        }

        public void Execute(IList<IFilter> filters, PipelineSettings pipelineSettings)
        {
            IEnumerable<SyndicationItem> items;

            if (pipelineSettings.Inputs != null)
            {
                // Get a list of feed items
                items = from feed in ParallelCrawl(pipelineSettings.Inputs)
                        from i in feed.Items
                        select i;
            }
            else
            {
                items = new SyndicationItem[0];
            }

            // Filter
            IEnumerable<SyndicationItem> filtered = items
                .Where(x => ApplyFilters(filters, x) == FilterAction.Include);

            // Remove duplicates
            IEnumerable<SyndicationItem> merged = Dedup(filtered);

            /* 这更像是一个"管道风格"的实现，而不是"管道模式"的实现。
            * 未来增加逻辑, 就继续处理, 还是要在这里硬编码处理merged
            *  (可能新增的管道 如:翻译, 分类, 添加情感分析管道, 添加图片提取管道等
            *
            * 真正的管道模式应该是什么样
            1. 应该有明确的管道步骤接口
            csharp
            // 当前缺失：没有统一的管道步骤接口
            public interface IPipelineStep<T>
            {
                IEnumerable<T> Process(IEnumerable<T> input, PipelineContext context);
                string Name { get; }
            }
            2. 应该有可配置的管道组合
            csharp
            // 当前缺失：无法通过配置定义处理流程
            public class Pipeline<T>
            {
                private readonly List<IPipelineStep<T>> _steps;

                public Pipeline(params IPipelineStep<T>[] steps)
                {
                    _steps = steps.ToList();
                }

                public IEnumerable<T> Execute(IEnumerable<T> input, PipelineContext context)
                {
                    var current = input;
                    foreach (var step in _steps)
                    {
                        current = step.Process(current, context);
                    }
                    return current;
                }
            }
            3. 应该有独立的管道步骤实现
            csharp
            // 当前缺失：处理逻辑直接写在Execute方法中，没有独立封装
            public class DownloadStep : IPipelineStep<SyndicationItem>
            {
                public string Name => "Download";

                public IEnumerable<SyndicationItem> Process(IEnumerable<SyndicationItem> input, PipelineContext context)
                {
                    // 独立的下载逻辑
                    var feeds = ParallelCrawl(context.Settings.Inputs);
                    return feeds.SelectMany(feed => feed.Items);
                }
            }

            public class FilterStep : IPipelineStep<SyndicationItem>
            {
                public string Name => "Filter";

                public IEnumerable<SyndicationItem> Process(IEnumerable<SyndicationItem> input, PipelineContext context)
                {
                    return input.Where(item => ApplyFilters(context.Filters, item) == FilterAction.Include);
                }
            }

             📊 当前架构 vs 真正管道模式的对比
            方面	    当前实现	            真正的管道模式
            处理流程	硬编码在Execute方法中	通过配置或代码组合
            扩展性	    需要修改核心代码	    实现新步骤即可
            可测试性	整个流程一起测试	    每个步骤独立测试
            职责分离	一个方法做多件事	    每个步骤单一职责
            灵活性	    处理顺序固定	        可动态调整顺序
            */
            if (pipelineSettings.Output != null)
            {
                // Save results
                var newFeed = new SyndicationFeed(merged.OrderBy(i => i.PublishDate));

                newFeed.Title = SyndicationContent.CreatePlaintextContent(pipelineSettings.Title);
                newFeed.Description = SyndicationContent.CreatePlaintextContent(pipelineSettings.Description);
                newFeed.LastUpdatedTime = DateTimeOffset.Now;

                using (XmlWriter writer = XmlWriter.Create(pipelineSettings.Output))
                {
                    newFeed.SaveAsAtom10(writer);
                }
            }
        }

        private IList<SyndicationFeed> ParallelCrawl(IList<string> urls)
        {
            var tasks = urls.Select(url =>
            {
                return httpClient.GetStreamAsync(url)
                    .ContinueWith(task =>
                    {
                        using (Stream responseStream = task.Result)
                        using (XmlReader reader = XmlReader.Create(responseStream))
                        {
                            var rss10 = new Rss10FeedFormatter();

                            if (rss10.CanRead(reader))
                            {
                                rss10.ReadFrom(reader);
                                return rss10.Feed;
                            }

                            return SyndicationFeed.Load(reader);
                        }
                    });
            });

            return Task.WhenAll(tasks).Result;
        }

        private static FilterAction ApplyFilters(IEnumerable<IFilter> filters, SyndicationItem item)
        {
            // By default all items are included
            FilterAction result = FilterAction.Include;

            if (filters != null)
            {
                // All filters are executed in order of definition
                foreach (var filter in filters)
                {
                    FilterAction action = filter.Apply(item);
                    if (action != FilterAction.None)
                    {
                        // The result is only changed when the filter "applies"
                        result = action;
                    }
                }
            }

            return result;
        }

        private static IEnumerable<SyndicationItem> Dedup(IEnumerable<SyndicationItem> items)
        {
            // Dedup feed items using Id, Title, and Link

            HashSet<string> titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<Uri> links = new HashSet<Uri>();

            foreach (SyndicationItem item in items)
            {
                if (ids.Add(item.Id) && titles.Add(item.Title.Text))
                {
                    SyndicationLink link = item.Links.SingleOrDefault(x => x.RelationshipType == "alternate");

                    if (link == null || links.Add(link.Uri))
                    {
                        yield return item;
                        /*
                        传统方法 vs Yield 方法：
                        传统方法：
                        输入 → 处理所有数据 → 创建列表 → 返回完整列表
                            ↓
                        内存中保存所有数据

                        Yield方法：
                        输入 → 处理第一个数据 → 返回第一个 → 处理第二个 → 返回第二个 → ...
                            ↓
                        内存中只保存当前处理的数据

                        *  🚀 Yield 在 RssPercolator 中的优势
                        *
                        *  1. 内存效率
                        *  csharp
                        *  // 假设有100万个RSS条目，但只有1000个不重复
                        *  // 传统方法：在内存中创建包含1000个元素的列表
                        *  // Yield方法：一次只在内存中保存1个元素
                        *  2. 流式处理
                        *  csharp
                        *  // 整个RssPercolator管道都是流式的：
                        *  // 下载 → 过滤 → 去重 → 排序 → 输出
                        *  //      ↓     ↓     ↓     ↓     ↓
                        *  //    yield  yield  yield yield 保存
                        *
                        *  // 数据像水流一样通过整个系统，不需要等待全部处理完成
                        *  3. 延迟执行
                        *  csharp
                        *  // 代码执行时机：
                        *  var deduped = Dedup(items);        // 还没有执行
                        *  var filtered = ApplyFilters(deduped); // 还没有执行
                        *  var sorted = filtered.OrderBy(...);   // 还没有执行
                        *
                        *  // 只有当最终枚举时，所有操作才真正执行：
                        *  foreach (var item in sorted)       // 现在开始执行！
                        *  {
                        *      SaveItem(item);                // 管道开始流动
                        *  }

                         */
                    }
                }
            }
        }

        private readonly HttpClient httpClient;
    }
}