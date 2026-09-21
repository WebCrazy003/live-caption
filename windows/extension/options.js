const field = document.getElementById("port");
chrome.storage.local.get({ port: 17653 }).then(({ port }) => { field.value = port; });
field.addEventListener("change", () => chrome.storage.local.set({ port: Number(field.value) || 17653 }));
