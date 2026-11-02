// H3 Yatzi – frontend
// TODO: hent spillets tilstand fra backend og vis terninger + pointtavle (DoD 7)

fetch("/api/health")
  .then((res) => res.json())
  .then((data) => console.log("Backend:", data.status))
  .catch(() => console.warn("Backend svarer ikke"));
