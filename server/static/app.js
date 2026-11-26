const labels = {
  queued: "В очереди",
  downloading: "Скачивается",
  completed: "Готово",
  failed: "Ошибка",
  interrupted: "Прервано",
};

const filters = {
  all: { matches: () => true, empty: "Очередь пока пуста" },
  active: { matches: ({ status }) => status === "queued" || status === "downloading", empty: "Активных заданий нет" },
  completed: { matches: ({ status }) => status === "completed", empty: "Готовых файлов пока нет" },
  issues: { matches: ({ status }) => status === "failed" || status === "interrupted", empty: "Заданий с ошибками или прерываниями нет" },
};

const connection = document.querySelector("#connection");
const form = document.querySelector("#add-form");
const input = document.querySelector("#urls");
const addButton = document.querySelector("#add-button");
const addMessage = document.querySelector("#add-message");
const startButton = document.querySelector("#start-button");
const queueMessage = document.querySelector("#queue-message");
const filterButtons = [...document.querySelectorAll(".filter")];
const empty = document.querySelector("#empty");
const emptyTitle = document.querySelector("#empty-title");
const emptyHelp = document.querySelector("#empty-help");
const jobList = document.querySelector("#jobs");
const jobTemplate = document.querySelector("#job-template");
const number = new Intl.NumberFormat("ru", { maximumFractionDigits: 1 });
const plurals = new Intl.PluralRules("ru");
const jobForms = { one: "задание", few: "задания", many: "заданий" };

let snapshot = null;
let online = false;
let currentFilter = "all";
let actionPending = false;
let refreshController = null;

function setText(element, text) {
  if (element.textContent !== text) {
    element.textContent = text;
  }
}

function setNotice(element, text, isError = false) {
  element.dataset.kind = isError ? "error" : "success";
  setText(element, text);
}

async function request(path, { method = "GET", body, controller = new AbortController() } = {}) {
  const headers = { Accept: "application/json" };
  if (method !== "GET") {
    headers["X-Video-Downloader"] = "1";
  }
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  const timeout = setTimeout(() => controller.abort(), 12000);
  try {
    let response;
    try {
      response = await fetch(path, {
        method,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
        signal: controller.signal,
        cache: "no-store",
      });
    } catch (error) {
      throw new Error(error.name === "AbortError"
        ? "Сервер не ответил вовремя. Проверьте очередь перед повтором."
        : "Не удалось связаться с сервером.");
    }

    if (!response.ok) {
      const data = await response.json().catch(() => null);
      const message = response.status === 401 ? "Войдите снова, обновив страницу." : `Ошибка сервера (${response.status}).`;
      throw new Error(typeof data?.detail === "string" ? data.detail : message);
    }
    return response.status === 204 ? null : await response.json().catch(() => {
      throw new Error("Сервер вернул ответ в неожиданном формате.");
    });
  } finally {
    clearTimeout(timeout);
  }
}

function updateControls() {
  const locked = !online || actionPending;
  addButton.disabled = locked || input.value.trim() === "";
  startButton.disabled = locked || snapshot.running || !snapshot.jobs.some(({ status }) => status === "queued");
  setText(startButton, snapshot?.running ? "Очередь запущена" : "Запустить очередь");
  for (const button of jobList.querySelectorAll("button")) {
    button.setAttribute("aria-disabled", String(locked));
  }
}

function formatSize(bytes) {
  if (!Number.isFinite(bytes) || bytes < 0) {
    return "";
  }
  const units = ["Б", "КиБ", "МиБ", "ГиБ", "ТиБ"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit += 1;
  }
  return `${number.format(value)} ${units[unit]}`;
}

function updateJob(row, job) {
  row.dataset.jobId = job.id;
  row.dataset.status = job.status;
  const title = job.title || job.url;
  setText(row.querySelector(".job-title"), title);

  const source = row.querySelector(".job-source");
  source.hidden = title === job.url;
  setText(source, source.hidden ? "" : job.url);

  const file = row.querySelector(".job-file");
  const fileText = [job.filename === title ? "" : job.filename, formatSize(job.size)].filter(Boolean).join(" · ");
  file.hidden = job.status !== "completed" || !fileText;
  setText(file, file.hidden ? "" : fileText);

  const issue = filters.issues.matches(job);
  const error = row.querySelector(".job-error");
  error.hidden = !issue || !job.error;
  setText(error, error.hidden ? "" : job.error);

  const progress = row.querySelector(".job-progress");
  progress.hidden = job.status !== "downloading";
  const percent = Number.isFinite(job.progress) ? Math.max(0, Math.min(100, job.progress)) : null;
  if (percent === null) {
    progress.removeAttribute("value");
  } else {
    progress.value = percent;
  }
  progress.setAttribute("aria-label", `Ход загрузки. ${title}`);
  const label = labels[job.status] || "Неизвестно";
  setText(row.querySelector(".job-status"), !progress.hidden && percent !== null ? `${label} · ${Math.round(percent)} %` : label);

  const download = row.querySelector("[download]");
  download.hidden = job.status !== "completed";
  if (!download.hidden) {
    download.href = `${jobPath(job.id)}/file`;
    download.setAttribute("aria-label", `Скачать файл. ${title}`);
  }

  const retry = row.querySelector('[data-action="retry"]');
  retry.hidden = !issue;
  retry.setAttribute("aria-label", `Вернуть в очередь. ${title}`);
  const remove = row.querySelector('[data-action="delete"]');
  remove.hidden = !issue && job.status !== "queued";
  remove.setAttribute("aria-label", `Удалить задание. ${title}`);
}

function jobPath(id) {
  return `/api/jobs/${encodeURIComponent(id)}`;
}

function renderQueue() {
  if (!snapshot) {
    setText(emptyTitle, connection.dataset.state === "offline" ? "Не удалось получить очередь" : "Загружаем очередь…");
    return;
  }

  const counts = Object.fromEntries(Object.entries(filters).map(([key, filter]) => [key, snapshot.jobs.filter(filter.matches).length]));
  for (const button of filterButtons) {
    const key = button.dataset.filter;
    button.setAttribute("aria-pressed", String(key === currentFilter));
    setText(button.querySelector("span"), String(counts[key]));
  }

  const filter = filters[currentFilter];
  const visible = snapshot.jobs.filter(filter.matches);
  const visibleIds = new Set(visible.map(({ id }) => String(id)));
  const rows = new Map();
  for (const row of [...jobList.children]) {
    if (visibleIds.has(row.dataset.jobId)) {
      rows.set(row.dataset.jobId, row);
    } else {
      if (row.contains(document.activeElement)) {
        filterButtons.find((button) => button.dataset.filter === currentFilter).focus();
      }
      row.remove();
    }
  }

  visible.forEach((job, index) => {
    const row = rows.get(String(job.id)) ?? jobTemplate.content.firstElementChild.cloneNode(true);
    updateJob(row, job);
    if (jobList.children[index] !== row) {
      jobList.insertBefore(row, jobList.children[index] ?? null);
    }
  });

  jobList.hidden = visible.length === 0;
  empty.hidden = visible.length > 0;
  setText(emptyTitle, filter.empty);
  setText(emptyHelp, currentFilter === "all" && visible.length === 0 ? "Добавьте ссылку на видео, чтобы создать задание." : "");
}

async function refresh() {
  if (actionPending || refreshController) {
    return;
  }
  const controller = new AbortController();
  refreshController = controller;
  try {
    const data = await request("/api/jobs", { controller });
    if (refreshController !== controller) {
      return;
    }
    if (!Array.isArray(data?.jobs) || typeof data.running !== "boolean") {
      throw new Error("Сервер вернул некорректную очередь.");
    }
    snapshot = data;
    online = true;
    connection.dataset.state = "online";
    setText(connection, "Сервер на связи");
  } catch {
    if (refreshController !== controller) {
      return;
    }
    online = false;
    connection.dataset.state = "offline";
    setText(connection, "Нет связи с сервером");
  } finally {
    if (refreshController === controller) {
      refreshController = null;
      renderQueue();
      updateControls();
    }
  }
}

async function changeQueue(path, options, message, success) {
  if (!online || actionPending) {
    return;
  }

  actionPending = true;
  const previousRefresh = refreshController;
  refreshController = null;
  previousRefresh?.abort();
  setNotice(message, "");
  updateControls();

  try {
    const result = await request(path, options);
    setNotice(message, success(result));
  } catch (error) {
    setNotice(message, error.message, true);
  } finally {
    actionPending = false;
    await refresh();
  }
}

input.addEventListener("input", updateControls);

form.addEventListener("submit", (event) => {
  event.preventDefault();
  const text = input.value;
  const urls = text.split(/\r?\n/).map((line) => line.trim()).filter(Boolean);
  if (!urls.length) {
    return;
  }
  if (urls.length > 100) {
    setNotice(addMessage, "Можно добавить до 100 ссылок за раз.", true);
    return;
  }
  if (urls.some((url) => !/^https?:\/\//i.test(url) || url.length > 4096)) {
    setNotice(addMessage, "Укажите ссылки HTTP или HTTPS длиной до 4096 символов.", true);
    return;
  }

  void changeQueue("/api/jobs", { method: "POST", body: { urls } }, addMessage, ({ added }) => {
    if (input.value === text) {
      input.value = "";
    }
    return added ? `Добавлено ${added} ${jobForms[plurals.select(added)]}.` : "Эти ссылки уже есть в очереди.";
  });
});

startButton.addEventListener("click", () => {
  void changeQueue("/api/start", { method: "POST" }, queueMessage, () => "Сервер принял команду запуска.");
});

for (const button of filterButtons) {
  button.addEventListener("click", () => {
    currentFilter = button.dataset.filter;
    renderQueue();
    updateControls();
  });
}

jobList.addEventListener("click", (event) => {
  const button = event.target.closest("button[data-action]");
  if (!button || button.getAttribute("aria-disabled") === "true") {
    return;
  }
  const path = jobPath(button.closest(".job").dataset.jobId);
  if (button.dataset.action === "retry") {
    void changeQueue(`${path}/retry`, { method: "POST" }, queueMessage, () => "Задание возвращено в очередь.");
  } else {
    void changeQueue(path, { method: "DELETE" }, queueMessage, () => "Задание удалено.");
  }
});

document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") {
    void refresh();
  }
});
window.addEventListener("online", () => void refresh());
setInterval(() => void refresh(), 2000);
void refresh();
