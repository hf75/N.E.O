# Web Search — apps that answer from the current web

A tool built with N.E.O. can look things up on the web while it runs, and Claude can look things up while it builds a tool.

"Build a research tool: a question, a button, the answer below with its sources." — twenty seconds later the tool is on the screen, and a question takes about five seconds to answer, with the pages the answer comes from.

Web search needs an Anthropic API key (`ANTHROPIC_API_KEY`). The other AI providers can still build apps that use it, as long as that key is set; they just cannot search while building.

## In generated apps

The AI knows these calls and uses them on its own when an app has to answer from the current web and it knows no free API for the data. Where it knows one (weather, exchange rates, NuGet, Wikipedia), it uses that: faster and free.

```csharp
using Neo.App;

// A text answer with the pages it comes from
WebAnswer answer = await AIWebSearch.AskAsync("How high is the ECB deposit rate?");
resultText.Text = answer.Text;
foreach (WebSource source in answer.Sources)
    Show(source.Title, source.Url);

// The same as data, for tables and lists
public record Launch(string Name, DateTime Date, string Site);

var launches = await AIWebSearch.AskAsync<List<Launch>>("What are the next 5 rocket launches worldwide?");
grid.ItemsSource = launches.Data;
```

- The answer comes in the language of the question. If the answer depends on a place, the question has to name it.
- `Sources` are the pages the answer comes from: the cited ones, or for data the ones the model says it used, and never a page the search did not really find. Empty means no page backs the answer. Apps have to show the sources: that is a condition of the API, and it lets the user check the answer.
- Data comes back in the given type: a record or class of strings, numbers, `bool`, `DateTime` and lists, or a list of it. The API guarantees the shape.
- It throws when the key is missing, the network is down, or the organization has switched web search off. Generated apps show the message.
- It works in exported programs and living apps too, on any computer that has the key in its environment.

The model is set under **Settings → Web Search Model** (default `claude-sonnet-5-5`); the list shows the models that can search.

What it costs: the API charges $10 per 1000 searches plus the tokens of the results. With Sonnet 5.5 that was about 5 US cents per question in our measurements, up to three searches per question, more than building a small tool. That is why the AI is told to call it on a user action and never on a timer, in a loop or on every keystroke.

## While Claude builds

Switch on **Settings → Let Claude search the web while building** and Claude can look things up while it writes the code: a service it does not know, a package that has changed, a fact from after its training. Up to three searches per try. What it searched and found is shown in the chat, and on the WPF host also while it happens.

It decides by itself when to search. We measured 21 tools on 2026-11-01:

| | Off | On |
|---|---|---|
| Time per tool | 13 s | 18 s |
| Cost per tool | 3.6 ct | 4.7 ct |
| Tools it searched for | | 2 of 21 |

It searched for the winner of this year's Ballon d'Or and for a Markdown package that works with Avalonia 12, and for none of the 15 ordinary tools. Only being offered the search takes the extra time, because the model then first thinks about whether to use it.

It is off by default for that reason and for this one: **what Claude reads on the web can influence code that runs on your computer**. Claude is told that web pages are information and never instructions, and that nothing may get into the code only because a page asks for it. That lowers the risk of a page trying to smuggle something in, but cannot rule it out. So code written after a search goes through the review dialog before it runs, with the searches and pages named on top, even when **Accept changes automatically** is on. If Claude did not search, nothing changes.

The search is frozen into the code: the Ballon d'Or tool shows what was true when it was built. For data that changes, ask for an app that fetches it when it runs.

If the model cannot search or the organization has switched it off, the tool is built without it and the chat says so.

## How it works

One entry in the `tools` list of the request to the Claude API (`web_search_20250305`): the API runs the searches itself. There is no agent loop, no SDK and no search provider to set up. The newer versions of the tool filter results through code execution; on a simple question that took more than twice as long, cost twice as much and left out the citations.

`Neo.Agents.Claude` holds it all: the option on `AnthropicTextChatAgent`, `ClaudeWebSearch` for the calls of generated apps, and `ClaudeWebSearchPlugin`, which makes it known to N.E.O. like the other plugin agents (see [[Architecture]]). `WebSearchHelper.cs` is compiled into every generated app, because the types an app names must also be there in an exported program.

## Limits

- Claude only. There is no search for images in the API.
- Only what search results show: no pages behind a login, no running of a page's JavaScript.
- A question costs money each time. A living app or an export spends the key of whoever runs it.
