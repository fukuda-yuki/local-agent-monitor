const EVENT_FIELDS = ["event_id", "run_id", "source_surface", "type", "occurred_at", "parent_event_id", "status", "content_state"];
const SPAN_FIELDS = ["id", "raw_record_id", "trace_id", "span_id", "parent_span_id", "span_ordinal", "operation", "category", "tool_name", "tool_type", "mcp_tool_name", "mcp_server_name", "mcp_server_hash", "agent_name", "request_model", "response_model", "input_tokens", "output_tokens", "total_tokens", "reasoning_tokens", "cache_read_tokens", "cache_creation_tokens", "status", "error_type", "finish_reasons", "conversation_id", "duration_ms", "start_time", "end_time", "projected_at"];
const AGENT_FIELDS = ["span_id", "agent_name", "agent_role", "caller_agent_span_id", "model", "started_at", "ended_at", "duration_ms", "input_tokens", "output_tokens", "total_tokens", "status", "child_agent_count", "agent_depth", "relationship_source", "relationship_confidence"];

function pick(source, fields) {
    const result = {};
    for (const field of fields) {
        if (source && source[field] !== undefined) result[field] = source[field];
    }
    return result;
}

export function evidenceTraceIds(detail) {
    const seen = new Set();
    const result = [];
    for (const run of Array.isArray(detail?.runs) ? detail.runs : []) {
        const traceId = typeof run?.trace_id === "string" && run.trace_id.length > 0 ? run.trace_id : null;
        if (traceId && !seen.has(traceId)) {
            seen.add(traceId);
            result.push(traceId);
        }
    }
    return result;
}

export function evidenceSelectionKey(kind, value, traceId) {
    if (kind === "event") return `event:${value?.event_id ?? ""}`;
    return `${kind}:${traceId ?? ""}:${value?.span_id ?? ""}`;
}

export async function collectAllSpanPages(fetchPage) {
    const spans = [];
    const seen = new Set();
    let cursor = null;
    while (true) {
        const page = await fetchPage(cursor);
        if (!page || !Array.isArray(page.items)) throw new Error("invalid_span_page");
        spans.push(...page.items);
        const next = canonicalCursor(page.next_cursor);
        if (next === null) return spans;
        if (seen.has(next)) throw new Error("repeated_span_cursor");
        if (cursor !== null && /^\d+$/.test(cursor) && /^\d+$/.test(next) && BigInt(next) <= BigInt(cursor)) throw new Error("non_progressing_span_cursor");
        seen.add(next);
        cursor = next;
    }
}

export function canonicalCursor(value) {
    if (value === null || value === undefined) return null;
    if (typeof value === "number") {
        if (!Number.isSafeInteger(value) || value < 0) throw new Error("invalid_span_cursor");
        return String(value);
    }
    if (typeof value !== "string") throw new Error("invalid_span_cursor");
    const trimmed = value.trim();
    if (!trimmed || trimmed.length > 512) throw new Error("invalid_span_cursor");
    return trimmed;
}

export function relationshipLabel(source, confidence) {
    if (source === "parent_span" && confidence === "exact") return "exact";
    if (source === "time_inferred" && confidence === "inferred") return "推定";
    return "判定不能";
}

function normalizedTime(value) {
    if (typeof value !== "string") return Number.POSITIVE_INFINITY;
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : Number.POSITIVE_INFINITY;
}

export function buildAgentForest(agents, parallelGroups) {
    const nodes = new Map((Array.isArray(agents) ? agents : []).map((agent) => [agent.span_id, { agent, children: [] }]));
    const roots = [];
    const orphans = [];
    for (const agent of Array.isArray(agents) ? agents : []) {
        const node = nodes.get(agent.span_id);
        if (agent.relationship_source === "unresolved") orphans.push(node);
        else if (agent.caller_agent_span_id === null || agent.caller_agent_span_id === undefined) roots.push(node);
        else if (nodes.has(agent.caller_agent_span_id) && agent.relationship_source !== "unresolved") nodes.get(agent.caller_agent_span_id).children.push(node);
        else orphans.push(node);
    }
    return { roots, orphans, parallelGroups: (Array.isArray(parallelGroups) ? parallelGroups : []).map((group) => Array.isArray(group) ? [...group] : []) };
}

function composeForest(trace, sourceOffset) {
    const legacyError = trace?.error;
    const graphError = trace?.graphError ?? legacyError ?? null;
    const spansError = trace?.spansError ?? legacyError ?? null;
    const graph = graphError ? {} : (trace?.graph ?? {});
    const agents = (Array.isArray(graph.agents) ? graph.agents : []).map((agent) => ({
        ...pick(agent, AGENT_FIELDS),
        relationshipLabel: relationshipLabel(agent.relationship_source, agent.relationship_confidence),
    }));
    const agentIds = new Set(agents.map((agent) => agent.span_id));
    const ownership = new Map((Array.isArray(graph.span_ownership) ? graph.span_ownership : []).map((item) => [item.span_id, item]));
    const spans = spansError ? [] : (Array.isArray(trace?.spans) ? trace.spans : []).map((span, index) => {
        const link = ownership.get(span?.span_id);
        const owner = link?.owning_agent_span_id && agentIds.has(link.owning_agent_span_id) ? link.owning_agent_span_id : null;
        return {
            ...pick(span, SPAN_FIELDS),
            owningAgentSpanId: owner,
            ownership: owner ? relationshipLabel(link.relationship_source, link.relationship_confidence) : "unresolved",
            sourceOrder: sourceOffset + index,
        };
    });
    return {
        traceId: trace.traceId,
        state: graphError && spansError ? "error" : "available",
        graphState: graphError ? "error" : "available",
        spanState: spansError ? "error" : "available",
        graphError: graphError ? pick(graphError, ["status", "error", "message"]) : null,
        spansError: spansError ? pick(spansError, ["status", "error", "message"]) : null,
        error: graphError && spansError ? pick(graphError, ["status", "error", "message"]) : null,
        presence: graph?.summary?.agent_presence ?? "undeterminable",
        relationshipQuality: graph?.summary?.relationship_quality ?? "undeterminable",
        agents,
        spans,
        parallelGroups: Array.isArray(graph.parallel_groups) ? graph.parallel_groups : [],
        graphWarnings: Array.isArray(graph.graph_warnings) ? graph.graph_warnings : [],
        agentForest: buildAgentForest(agents, graph.parallel_groups),
    };
}

export function composeEvidence(sessionDetail, traces) {
    let sourceOrder = 0;
    const forests = (Array.isArray(traces) ? traces : []).map((trace) => {
        const forest = composeForest(trace, sourceOrder);
        sourceOrder += forest.spans.length;
        return forest;
    });
    const timeline = [];
    for (const forest of forests) {
        for (const span of forest.spans) {
            timeline.push({ kind: "span", id: span.span_id, traceId: forest.traceId, time: span.start_time ?? null, ownership: span.ownership, owningAgentSpanId: span.owningAgentSpanId, value: span, sourceOrder: span.sourceOrder });
        }
    }
    for (const event of Array.isArray(sessionDetail?.events) ? sessionDetail.events : []) {
        const sanitized = pick(event, EVENT_FIELDS);
        timeline.push({ kind: "event", id: sanitized.event_id, traceId: null, time: sanitized.occurred_at ?? null, ownership: "session_unowned", owningAgentSpanId: null, value: sanitized, sourceOrder: sourceOrder++ });
    }
    timeline.sort((left, right) => normalizedTime(left.time) - normalizedTime(right.time) || left.sourceOrder - right.sourceOrder);
    return { graphState: forests.length === 0 ? "unavailable" : "available", forests, timeline };
}

export function evidenceInspector(selection) {
    const value = selection?.value ?? {};
    if (selection?.kind === "agent") return pick(value, AGENT_FIELDS);
    if (selection?.kind === "event") return pick(value, EVENT_FIELDS);
    const span = pick(value, SPAN_FIELDS);
    return {
        ...span,
        skill_name: value.skill_name ?? "利用不可",
        skill_path: value.skill_path ?? "利用不可",
        skill_version: value.skill_version ?? "利用不可",
        test_result: value.test_result ?? "利用不可",
        review_result: value.review_result ?? "利用不可",
    };
}

export function evidenceGateLinks(session, events) {
    const sanitized = (Array.isArray(events) ? events : []).map((event) => pick(event, EVENT_FIELDS));
    const terminalTypes = new Set(["session.shutdown", "session.task_complete", "SessionEnd", "Stop"]);
    const terminal = sanitized.find((event) => terminalTypes.has(event.type));
    return {
        terminal: terminal ? `event:${terminal.event_id}` : null,
        errors: sanitized.filter((event) => event.status === "error").map((event) => `event:${event.event_id}`),
    };
}

export function createEvidenceLoadCoordinator(onState) {
    let generation = 0;
    return {
        async load(sessionId, loader) {
            const current = ++generation;
            onState({ sessionId, loading: true, data: null });
            const data = await loader(sessionId);
            if (current !== generation) return false;
            onState({ sessionId, loading: false, data });
            return true;
        },
        invalidate() { generation += 1; },
    };
}

export function evidenceBrowserScript() {
    return String.raw`
function evidenceTraceIdsClient(detail){const seen=new Set(),ids=[];(Array.isArray(detail&&detail.runs)?detail.runs:[]).forEach(run=>{const id=typeof run.trace_id==="string"&&run.trace_id?run.trace_id:null;if(id&&!seen.has(id)){seen.add(id);ids.push(id);}});return ids;}
function evidenceKey(kind,value,traceId){return kind==="event"?"event:"+(value&&value.event_id||""):kind+":"+(traceId||"")+":"+(value&&value.span_id||"");}
async function runReviewEvidenceLink(sessionId,event,load,getState,apply){const before=getState();const loaded=await load();const after=getState();if(!loaded||before.sessionId!==sessionId||after.sessionId!==sessionId||before.generation!==after.generation)return false;apply(event);return true;}
function evidenceCursor(value){if(value===null||value===undefined)return null;if(typeof value==="number"){if(!Number.isSafeInteger(value)||value<0)throw new Error("invalid_span_cursor");return String(value);}if(typeof value!=="string")throw new Error("invalid_span_cursor");const trimmed=value.trim();if(!trimmed||trimmed.length>512)throw new Error("invalid_span_cursor");return trimmed;}
async function loadSessionEvidence(detail,request){const traces=[];for(const traceId of evidenceTraceIdsClient(detail)){const trace={traceId,graph:null,graphError:null,spans:[],spansError:null};const graphResponse=await request("/api/session-evidence/traces/"+encodeURIComponent(traceId)+"/agent-graph");if(graphResponse.status===200)trace.graph=graphResponse.body;else trace.graphError={status:graphResponse.status,error:graphResponse.body&&graphResponse.body.error};let cursor=null;const seen=new Set();do{const path="/api/session-evidence/traces/"+encodeURIComponent(traceId)+"/spans?limit=200"+(cursor!==null?"&after="+encodeURIComponent(cursor):"");const page=await request(path);if(page.status!==200||!Array.isArray(page.body&&page.body.items)){trace.spansError={status:page.status,error:page.body&&page.body.error};break;}trace.spans.push(...page.body.items);let next;try{next=evidenceCursor(page.body.next_cursor);}catch{trace.spansError={status:502,error:"invalid_span_page"};break;}if(next!==null&&seen.has(next)){trace.spansError={status:502,error:"repeated_span_cursor"};break;}if(next!==null&&cursor!==null&&/^\d+$/.test(cursor)&&/^\d+$/.test(next)&&BigInt(next)<=BigInt(cursor)){trace.spansError={status:502,error:"non_progressing_span_cursor"};break;}if(next!==null)seen.add(next);cursor=next;}while(cursor!==null);traces.push(trace);}return traces;}
function evidenceRelationship(item){return item&&item.relationship_source==="parent_span"&&item.relationship_confidence==="exact"?"exact":item&&item.relationship_source==="time_inferred"&&item.relationship_confidence==="inferred"?"推定":"判定不能";}
function evidenceTime(value){const parsed=Date.parse(value||"");return Number.isFinite(parsed)?parsed:Number.POSITIVE_INFINITY;}
function evidenceForestClient(agents,parallelGroups){const nodes=new Map(agents.map(agent=>[agent.span_id,{agent,children:[]}])),roots=[],orphans=[];agents.forEach(agent=>{const node=nodes.get(agent.span_id);if(agent.relationship_source==="unresolved")orphans.push(node);else if(agent.caller_agent_span_id==null)roots.push(node);else if(nodes.has(agent.caller_agent_span_id))nodes.get(agent.caller_agent_span_id).children.push(node);else orphans.push(node);});return{roots,orphans,parallelGroups:Array.isArray(parallelGroups)?parallelGroups.map(group=>Array.isArray(group)?group.slice():[]):[]};}
function evidenceView(detail,traces){let ordinal=0;const forests=traces.map(trace=>{const graph=trace.graph||{},sourceAgents=Array.isArray(graph.agents)?graph.agents:[],agentIds=new Set(sourceAgents.map(a=>a.span_id)),owners=new Map((Array.isArray(graph.span_ownership)?graph.span_ownership:[]).map(o=>[o.span_id,o])),agentForest=evidenceForestClient(sourceAgents,graph.parallel_groups),agents=[];const walk=(node,depth)=>{agents.push({...node.agent,displayDepth:depth});node.children.forEach(child=>walk(child,depth+1));};agentForest.roots.forEach(root=>walk(root,0));agentForest.orphans.forEach(orphan=>agents.push({...orphan.agent,displayDepth:0}));const spans=(trace.spansError?[]:(Array.isArray(trace.spans)?trace.spans:[])).map(span=>{const link=owners.get(span.span_id),owner=link&&agentIds.has(link.owning_agent_span_id)?link.owning_agent_span_id:null;return{value:span,owner,relationship:owner?evidenceRelationship(link):"判定不能",ordinal:ordinal++};});return{traceId:trace.traceId,state:trace.graphError&&trace.spansError?"error":"available",error:trace.graphError&&trace.spansError?(trace.graphError||trace.spansError):null,graphState:trace.graphError?"error":"available",spanState:trace.spansError?"error":"available",graphError:trace.graphError,spansError:trace.spansError,presence:graph.summary&&graph.summary.agent_presence,agents,spans,agentForest};});const timeline=[];forests.forEach(f=>f.spans.forEach(s=>timeline.push({kind:"span",id:s.value.span_id,time:s.value.start_time,relationship:s.relationship,owner:s.owner,value:s.value,ordinal:s.ordinal,traceId:f.traceId})));(Array.isArray(detail&&detail.events)?detail.events:[]).forEach(event=>timeline.push({kind:"event",id:event.event_id,time:event.occurred_at,relationship:"Session / unowned",owner:null,value:event,ordinal:ordinal++,traceId:null}));timeline.sort((a,b)=>evidenceTime(a.time)-evidenceTime(b.time)||a.ordinal-b.ordinal);return{forests,timeline};}`;
}
